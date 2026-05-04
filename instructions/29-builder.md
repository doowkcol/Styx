# 29 — StyxBuilder

Whole-base **upgrade / downgrade / repair** on the player-placed blocks tracked inside their **current land claim**. Stand inside YOUR LCB, run `/build` (or `/m → Builder`), pick a block grade, pick an action, confirm. Material costs are drawn from owned storage crates inside the same claim.

Three-stage UI keeps the mixed-tier base case clean: pick a grade (Wood / Cobblestone / Concrete / Steel / Basic / Other), then pick Repair / Upgrade / Downgrade — only blocks of that grade are touched. Workstations, storage crates, doors and other tile-entity blocks are excluded by default so a Wood Storage Crate doesn't get auto-upgraded to a steel crate.

Cost gating is per-perm: free, 25/50/75 % discount, or full. Highest-tier perm wins.

## Commands

| Command | Perm | What |
|---|---|---|
| `/build` | `styx.builder.use` | Open the Builder picker UI (same as `/m → Builder`) |
| `/repair scan\|confirm\|cancel` | `styx.builder.repair` | Scan tracked claim blocks → confirm to fix all damage. Cost = sum of `RepairItems × damage_ratio`, drawn from claim crates. |
| `/upgrade scan\|confirm\|cancel` | `styx.builder.upgrade` | Promote each tracked block to its `UpgradeBlock` target. Cost = sum of `UpgradeBlock.Item × ItemCount` minus discount. |
| `/downgrade scan\|confirm\|cancel` | `styx.builder.downgrade` | Revert each tracked block to the previous tier in its upgrade chain. Free by default; configurable. |
| `/build labels` | `styx.builder.use` | Diagnostic — resolves `builder_tier_0..5` localization keys server-side |

The chat commands operate on **every** block grade in your claim when called without a filter. The UI flow restricts to the grade you picked.

## Permissions

| Perm | What |
|---|---|
| `styx.builder.use` | Open the `/m → Builder` UI and run `/build` |
| `styx.builder.repair` | Run `/repair scan/confirm` |
| `styx.builder.repair.free` | `/repair` costs nothing |
| `styx.builder.upgrade` | Run `/upgrade scan/confirm` |
| `styx.builder.upgrade.free` | `/upgrade` costs nothing (also forces `/downgrade` free regardless of `CostDowngrade`) |
| `styx.builder.upgrade.discount25` | 25 % discount on upgrade material costs |
| `styx.builder.upgrade.discount50` | 50 % discount |
| `styx.builder.upgrade.discount75` | 75 % discount |
| `styx.builder.downgrade` | Run `/downgrade scan/confirm` |

Discount perms stack by **highest wins** — granting both `.discount25` and `.discount75` to a group means the group gets 75 % off.

## Config — `configs/StyxBuilder.json`

| Field | Default | Meaning |
|---|---|---|
| `ScanRadiusOverride` | `0` | Override the LCB protect radius for testing. `0` uses vanilla `GameStats.LandClaimSize`. |
| `RequireSecureContainerOwnership` | `false` | When true, only pull materials from lockable containers where the player is owner / on the ACL. Default behaviour treats LCB ownership as the security boundary, so any crate inside the claim counts. |
| `ScanResultTtlSeconds` | `300` | After scan, how long the operator has to `confirm`. After expiry, re-scan. |
| `FlushIntervalSeconds` | `30` | How often the dirty block tracker flushes to disk. |
| `SkipTileEntityBlocksOnUpgrade` | `true` | When true, `/upgrade` and `/downgrade` skip TE blocks (storage crates, workstations, doors, lights, power sources). Their TE state would be wiped on swap. |
| `CostDowngrade` | `false` | When true, `/downgrade` charges the same as upgrading INTO the target tier (with discount applied). `styx.builder.upgrade.free` always grants free downgrade regardless. |

## Mechanics

### Block grade buckets

The tier picker collapses every tracked block into one of six grade buckets keyed off `block.blockMaterial.id`:

| Grade | Examples |
|---|---|
| Basic | Frames, plant fibre, scrap iron etc. |
| Wood | Wood frames, planks, log spikes |
| Cobblestone | Cobblestone, flagstone variants |
| Concrete | Concrete, rebar |
| Steel | Steel, hardened steel |
| Other | Anything that doesn't slot into the above (e.g. modlet-added materials) |

Big bases would have overflowed a per-block-id picker with thousands of shape variants. Six grade buckets stay readable regardless of base size.

### Repair cost breakdown

Damage ratio per block = `damage / Block.MaxDamage`. Required item count = `RepairItems[i].Count × damage_ratio`, rounded up. Total = sum across all damaged tracked blocks of the picked grade. Charged from claim crates at confirm time.

### Material drawing rules

Materials are **only** drawn from:

1. Storage crates inside the LCB protect area
2. Owned by the operator (or, with `RequireSecureContainerOwnership=false`, any container — the LCB ownership is the security boundary)

If totals can't be met, confirm fails and reports the shortfall in chat — nothing is touched.

### Hot tips

- Run `/repair scan` before big base expansions so you know the cost.
- `/downgrade` is cheap demolition without losing material progression — useful to revert a wing back to Wood if you over-built early.
- `/build labels` is admin-only diagnostics; check it after editing localization to verify the server saw your changes.
