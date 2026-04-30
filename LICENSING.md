# Licensing

Styx ships under a **two-tier licence**. Most files are MIT (do whatever you
want). Some files — the polished plugins that distinguish a production
Styx server from a vanilla one — are **source-available but restricted**:
inspect and personal-use freely, but no redistribution and no use in paid
hosting offerings without a commercial licence.

| Tier | Files | Licence | TL;DR |
|---|---|---|---|
| **Framework + reference plugins** | `Styx.Core.*` and `.cs` files marked `MIT` in their header | [MIT](./LICENSE) | Use, modify, redistribute, commercialise — anything. |
| **Restricted plugins** | `.cs` files marked `LicenseRef-Styx-Plugin-Restricted` in their header | [Custom](./LICENSE-PLUGINS) | Personal use + modification only. **No redistribution, no resale, no paid-hosting use** without a commercial licence. |

## How to tell which tier a file is in

Every `.cs` file begins with an SPDX header:

- `// SPDX-License-Identifier: MIT` → permissive ([LICENSE](./LICENSE))
- `// SPDX-License-Identifier: LicenseRef-Styx-Plugin-Restricted` → restricted ([LICENSE-PLUGINS](./LICENSE-PLUGINS))

If a file has no header it is provisionally treated as **restricted** — please
open an issue so we can fix it.

## Why two tiers?

The **framework** and **reference plugins** are open forever. That's the
adoption story — anyone can build on Styx without legal friction, including
commercial server hosts and other modders who want to learn the patterns.

The **restricted plugins** are the polished work that took the most time and
distinguishes a production Styx server: perm-tiered crafting with idle-fuel
auto-shutdown, group-mapped donor perks with per-player toggle UI, the HUD
and menu UI showcases, the PvP and admin tooling. Source is open for
inspection and personal use; commercial redistribution requires a separate
licence so the work isn't repackaged and resold under someone else's name.

## Plugin tier list

### MIT — reference + foundational

The plugins everyone needs to get started, plus the demos that teach you how
to write your own:

| Plugin | What |
|---|---|
| `HelloSource` | Minimal "hello world" plugin — read this first |
| `GameDataDemo` | Demo of the runtime GameData mutation API |
| `StyxInputProbe` | Input event whisper for testing the input subsystem |
| `StyxGameRefs` | Registry dump utility (items / blocks / entities / buffs) |
| `Kit` | Claimable starter / VIP / themed item kits |
| `StyxTeleport` | `/sethome`, nearest trader, last death |
| `StyxBackpack` | Persistent personal storage + perm-tiered death bag |
| `ChatTags` | Group-priority `[Tag]` chat prefix |
| `WelcomeMessage` | Permission-gated welcome message on spawn |
| `ZombieLoot` | Perm-tiered loot bag drops on zombie kill |
| `PermManager` | `/perm` chat commands |
| `StyxPerms` | Perm system frontend |
| `PermEditor` | Perm-editor UI |

### Restricted — showcase / production

The polished plugins that make a Styx server feel finished:

| Plugin | What |
|---|---|
| `StyxCrafting` | Perm-tiered forge speed + output bonus + idle-fuel auto-shutdown |
| `DonorPerks` | Group-mapped buffs + per-player toggle UI |
| `StyxZombieRadar` | Live per-player zombie-count HUD section |
| `StyxZombieHealth` | Crosshair entity-health HUD — name + HP of what you're aimed at |
| `StyxHud` | Top-left HUD: players, rank, wipe + restart countdown |
| `StyxMenu` | Interactive action menu — heal-full, water, teleport, server info |
| `ServerRestartManager` | Scheduled daily restarts with countdown |
| `Godmode` | Styx-internal damage immunity flag |
| `Vanish` | Invisible + AI-ignore admin tool |
| `Reflect` | Damage-reflect modes (off / shield / back / double) |
| `AdminRadar` | Through-walls 5-category entity radar |
| `AdminTools` | Sub-menu launcher for vanish + radar toggles |

## Commercial licensing

If you want to:

- Bundle Styx restricted plugins into a paid server-hosting offering, or
- Redistribute via a mod portal or template marketplace, or
- Build a commercial product on top of Styx that includes restricted plugins,

contact **jacklockwood@outlook.com** to discuss a commercial licence. Pricing
depends on use case; small-server hosts with modest playerbases get heavy
discounts (often free) — we want Styx in the wild.

## Contributions

PRs and issues against the original repository are welcome under either tier.
By submitting a contribution you agree it will be licensed under the same tier
as the file it modifies. We're not asking you to sign a CLA.
