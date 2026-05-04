# Styx

A **server-side plugin framework for 7 Days to Die V2.6 dedicated servers.**

- **Server-side only.** No client install required. Players join with vanilla / console-style clients.
- **EAC-compatible.** Anti-cheat stays enabled. The framework adds nothing to the client.
- **Hot-reload `.cs` plugins.** Drop a plugin file in `Mods/Styx/plugins/`, save changes — the framework compiles and reloads it live. No restart for plugin code, no build step.
- **Single-file plugins.** Plugin authors embed buff defs, XUi panels, window-group registrations and localization rows directly in the `.cs` source as `/* @styx-* */` block comments. The framework extracts them at boot and writes the canonical `Config/buffs.xml`, `Config/XUi/windows.xml`, `Config/XUi/xui.xml`. Operators drop one file, restart, done — no XML editing, no manual merging. See [`docs/plugin-authoring.md`](./docs/plugin-authoring.md).
- **Batteries included** — permissions with group inheritance, lifecycle hooks with per-plugin attribution, chat commands with parsing + cooldowns, scheduler, persistent data stores, auto-defaulting configs, an XUi panel-mounting layer (server-driven cvars drive HUDs and interactive windows), and a built-in profiler.

Built on the native `IModApi` + Harmony. Plugin source is compiled by Roslyn at boot and on save. Ships with 30+ reference plugins covering economy, progression, base management, perm-tiered perks, admin tooling and HUDs — the whole thing is the framework you'd build for yourself the third time you wrote a server-side mod.

Why it exists: nothing else in the 7DTD ecosystem ships this combination — especially the EAC-on + no-client-install constraint that lets console-style players join a modded server, and the live hot-reload of plugin source.

---

## Quick start

1. Drop the `Styx` folder into your server's `Mods/` directory.
2. Start the server. Configs auto-create with sane defaults under `Mods/Styx/configs/`.
3. Make yourself admin — see [`instructions/00-bootstrap.md`](./instructions/00-bootstrap.md).
4. Start exploring with `/m` (the launcher) or any of the per-plugin commands.

Full per-plugin operator reference: [`instructions/README.md`](./instructions/README.md).

---

## What ships in the box

**Player-facing — survival quality of life**
- `/m` action launcher · `/menu` action menu (heal, water, teleport, info)
- `/build` whole-base auto-upgrade / downgrade / repair on tracked claim blocks (perm-tiered free / discounted / full-cost)
- `/shield` sanctuary stealth zone bound to your land claim — zombies don't notice you while inside (auto-suspends during blood moon)
- `/nvg` toggle personal night vision (pinned-to-camera light source)
- `/sethome` × 3 · nearest trader · last death teleport
- `/b` persistent personal backpack + perm-tiered death bag
- `/kit` claimable starter / VIP / themed item kits
- `/zloot` perm-tiered loot bag drops on zombie kill (Romero-tuned, 18+ zombie classes themed)
- `/buffs` perm-gated buff perks (toggle and on-demand-cooldown flavours, per-player UI)
- `/crafting` perm-tiered forge speed + idle-fuel auto-shutdown
- `/zhealth` crosshair entity-health HUD readout
- `/radar` per-player live zombie-count HUD section
- `/wm` perm-gated welcome messages on spawn
- `[Tag]` chat-prefix system with group priority

**Player-facing — economy / progression**
- `/balance` `/pay` `/eco` per-player virtual currency wallet
- `/rewards` configurable earn engine (kills, loot, harvest, quests, login, online time)
- `/xp` server XP + level system with milestone group promotions
- `/shop` `/s` categorised paginated item shop — pay with Credits
- `/sell` sell loot to the server bank from anywhere

**Admin tools**
- `/perm` `/m → Perm Editor` group → plugin → toggle perm UI (sliding window over long lists)
- `/aradar` through-walls 5-category entity radar
- `/vanish` invisible + AI-ignore
- `/reflect` damage-reflect modes
- `/srm` scheduled daily restarts with countdown
- `/prof` chat-rendered profiler (hooks / commands / timers / patches / GC) — zero-overhead when off

**HUD / UI**
- Top-left HUD showing players, rank, wipe + restart countdown, level / XP / currency
- Per-plugin XUi panels (zombie radar, zombie health, builder, shield, action menu, perm editor)

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

Styx is **production-grade for single-server use**. It runs the public **Styx Framework Test Server** (PvE, EU, Navezgane — search the in-game browser, or direct connect `162.19.126.139:26900`) day-to-day. Operator Discord: https://discord.gg/sV3kTB5n2e.

It has not yet been stress-tested across many concurrent server admins or large-scale community deployments — partner-server testing welcome (open an issue).

The framework targets **7 Days to Die V2.6**. V2.7 / V3 compatibility will be tracked as those versions ship; expect packet shapes and engine internals to drift between point releases.

---

## For plugin authors

Start with **[`docs/plugin-authoring.md`](./docs/plugin-authoring.md)** — covers the embedded manifest system, every supported `/* @styx-* */` section (buffs, XUi windows, window-group registration, localization), the synthesis lifecycle, the dev-mode toggle, and the common gotchas.

Reference patterns to copy from:
- **Minimal plugin**: `plugins/HelloSource.cs`
- **Permission-gated command**: `plugins/Kit.cs`
- **Perm-tiered behaviour**: `plugins/ZombieLoot.cs` or `plugins/StyxCrafting.cs`
- **Single-file plugin with embedded buff**: `plugins/StyxNvg.cs` — toggle command + `@styx-buffs` block
- **Single-file plugin with embedded UI panel + buff**: `plugins/StyxShield.cs` — `@styx-xui-windows` + `@styx-xui-window-group toolbelt` + `@styx-buffs` in one file
- **Interactive menu (input capture)**: `plugins/StyxMenu.cs`
- **Multi-stage UI with sliding window**: `plugins/PermEditor.cs` (group → plugin → perm flow)
- **Harmony patches via the framework**: `plugins/StyxShield.cs` + `Styx.Hooks.FirstParty.ShieldGuard` (filters AI calls in-engine)
- **GameData mutation**: `plugins/GameDataDemo.cs`

The `instructions/` folder is the **operator** reference (per-plugin commands, perms, configs). Plugin-author docs live in `docs/`.

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
