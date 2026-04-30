# Styx

A **Carbon-style server-side modding framework for 7 Days to Die V2.6**.

- **Server-side only.** No client install required. Players join with vanilla / console-style clients.
- **EAC-compatible.** Anti-cheat stays enabled. The framework adds nothing to the client.
- **Hot-reload `.cs` plugins.** Save the file, the plugin reloads. No restart for plugin code.
- **Permission system, perm-tier UI, hooks, scheduler, XUi panel mounting, indexed-localization HUD pattern, configs, persistent data stores** — all wired up.

Inspired by [Carbon](https://github.com/CarbonCommunity/Carbon.Core) (Rust) and [uMod / Oxide](https://umod.org). Built because nothing else in the 7DTD ecosystem does this combination — especially the EAC-on + no-client-install constraint that lets console-style players join a modded server.

---

## Quick start

1. Drop the `Styx` folder into your server's `Mods/` directory.
2. Start the server. Configs auto-create with sane defaults under `Mods/Styx/configs/`.
3. Make yourself admin — see [`instructions/00-bootstrap.md`](./instructions/00-bootstrap.md).
4. Start exploring with `/m` (the launcher) or any of the per-plugin commands.

Full per-plugin operator reference: [`instructions/README.md`](./instructions/README.md).

---

## What ships in the box

**Player-facing**
- `/m` action launcher · `/menu` action menu (heal, water, teleport, info)
- `/sethome` × 3 · nearest trader · last death teleport
- `/b` persistent personal backpack + perm-tiered death bag
- `/kit` claimable starter / VIP / themed item kits
- `/zloot` perm-tiered loot bag drops on zombie kill (Romero-tuned, 18+ zombie classes themed)
- `/donor` group-mapped buffs with per-player toggle UI
- `/crafting` perm-tiered forge speed + idle-fuel auto-shutdown
- `/zhealth` crosshair entity-health HUD readout
- `/radar` per-player live zombie-count HUD section
- `/wm` perm-gated welcome messages on spawn
- `[Tag]` chat-prefix system with group priority

**Admin tools**
- `/aradar` through-walls 5-category entity radar
- `/vanish` invisible + AI-ignore
- `/god` damage immunity
- `/reflect` damage-reflect modes
- `/srm` scheduled daily restarts with countdown

**HUD / UI**
- Top-left HUD showing players, rank, wipe + restart countdown
- Per-plugin XUi panels (zombie radar, zombie health, action menu)

**Dev / reference**
- `/hello` minimal "hello world" plugin
- `/src` source-display demo
- `/gd` GameData mutation API demo
- `/input` input-event whisper for testing
- `/gamerefs` registry dump utility (items / blocks / entities / buffs)

---

## Licensing

Styx ships under a **two-tier licence**. Most files are MIT (do whatever you want). Some plugins — the polished ones — are **source-available but restricted**: personal use freely, but no commercial redistribution.

- [`LICENSE`](./LICENSE) — MIT (framework + reference plugins)
- [`LICENSE-PLUGINS`](./LICENSE-PLUGINS) — Custom restricted licence (showcase plugins)
- [`LICENSING.md`](./LICENSING.md) — Plain-English summary, plus the per-plugin tier list

Every `.cs` file declares its tier in an SPDX header on line 1.

Commercial / hosting-partnership enquiries: **jacklockwood@outlook.com**.

---

## Status

Styx is **production-grade for single-server use**. It runs the [REKT 7DTD Romero PvP server](https://discord.gg/rekt) day-to-day. It has not yet been stress-tested across many concurrent server admins or large-scale community deployments — partner-server testing welcome (open an issue).

The framework targets **7 Days to Die V2.6**. V2.7 / V3 compatibility will be tracked as those versions ship; expect packet shapes and engine internals to drift between point releases.

---

## For plugin authors

The `instructions/` folder is operator-focused. Plugin authoring docs (framework internals, hook catalogue, capabilities, engine-surface notes) live in the original development repository and will be split into a `docs/` folder here once the public repo settles.

Patterns to start from:
- **Minimal plugin**: `plugins/HelloSource.cs`
- **Permission-gated command**: `plugins/Kit.cs`
- **Perm-tiered behaviour**: `plugins/ZombieLoot.cs` or `plugins/StyxCrafting.cs`
- **Player-facing UI panel**: `plugins/StyxHud.cs` or `plugins/StyxZombieHealth.cs`
- **Interactive menu (input capture)**: `plugins/StyxMenu.cs`
- **GameData mutation**: `plugins/GameDataDemo.cs`

---

## Contributing

PRs, bug reports, and ideas welcome. Please:
- Match the licence tier of the file you're editing (don't relicense restricted plugins as MIT, or vice versa).
- Keep plugin descriptions in the comment block at the top of the file.
- New plugins should ship with a corresponding doc in `instructions/`.

By submitting a contribution you agree it will be licensed under the same tier as the file it modifies. No CLA.

---

## Author

**Doowkcol** (Jack Lockwood) — `jacklockwood@outlook.com`
