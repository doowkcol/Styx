# 11 — Godmode

> **🚫 DISABLED — shipped as `Godmode.cs.disabled`. Will not load.**
>
> Server-side godmode is fundamentally blocked by the client-authoritative
> death-prediction path on V2.6 dedicated servers. The framework patches
> in `Styx.Core/Hooks/FirstPartyPatches.cs` correctly cancel server-side
> damage paths (verified — `Kill()` / `OnEntityDeath()` never fire), but
> the client locally predicts the death and shows the respawn screen
> regardless. We've tried six patch points covering the entire damage
> pipeline; closing the last gap appears to require either a client
> companion mod (which Styx avoids by design) or an engine hook that
> hasn't been identified yet.
>
> File renamed to `.disabled` to make the no-op state explicit — the
> plugin watcher only picks up `.cs` files, so it won't load. The
> framework's IsGodmode/SetGodmode API on `StyxCore` remains in place
> for any future plugin that wants to consult it, but no plugin
> currently does.
>
> **Future work:** if a server-only client-death-suppression hook is
> ever discovered, or the project decides to relax the no-client-mod
> constraint for admin tooling, the plugin can be re-enabled by simply
> renaming back to `.cs`. Background context for the resumption:
> `STYX_V26_ENGINE_SURFACE.md §1.2 / §1.7 / §1.8`.
>
> ---
>
> The doc below describes the intended surface for reference. Don't
> rely on it in production today.

---

Styx-internal damage immunity flag. Intended to be consulted by framework Harmony patches via `StyxCore.IsGodmode(entityId)` — does NOT use the vanilla `EntityPlayer.IsGodMode` flag (which gets reset by client sync).

## Commands (registered, no-op-equivalent today)

| Command | Perm | What |
|---|---|---|
| `/god` | `styx.god.use` | Toggle godmode on yourself |
| `/god on` | `styx.god.use` | Force on |
| `/god off` | `styx.god.use` | Force off |

## Permissions

| Perm | What |
|---|---|
| `styx.god.use` | Allowed to toggle godmode on yourself |

## Config — `configs/Godmode.json`

```json
{
  "Players": []
}
```

`Players` is the persisted list of platform IDs the plugin re-arms on every join/spawn. The plugin's runtime state is the in-memory `StyxCore` set; this config just decides who gets re-armed automatically.

## Mechanics — current state (2026-04-28)

`StyxCore.IsGodmode(entityId)` is consulted by framework Harmony patches in `Styx.Core/Hooks/FirstPartyPatches.cs`:

1. `NetPackageDamageEntity.ProcessPackage` Prefix
2. `EntityAlive.damageEntityLocal` Prefix
3. `EntityAlive.ProcessDamageResponse` Prefix
4. `EntityAlive.OnUpdateEntity` Prefix (auto-kill HP-nudge guard)
5. `PlayerEntityStats.UpdatePlayerHealthOT` Prefix (buff DoT)
6. `NetPackageEntityStatChanged.ProcessPackage` Prefix (client-pushed Health sync)
7. `NetPackageGameMessage.ProcessPackage` Prefix (suppress "X died" GMSG)
8. `EntityAlive.Died` setter Prefix (block Died counter increment)

Live-test result on a dedicated server with all of these active: server-side damage paths cancel as logged, but the **client-side death prediction still fires**. Player sees themselves die, hits respawn screen, server happily respawns them — meaning the "godmode" never engaged from the player's perspective.

The unsolved gap is the **client-authoritative death prediction** path. To fully close it would likely require a companion client-side mod (which Styx is explicitly avoiding to remain server-only + EAC-compatible).

See `STYX_V26_ENGINE_SURFACE.md §1.2` (the `isEntityRemote` trap) and `§1.7`/`§1.8` (the patches that have been tried so far) for the technical detail.

## Notes

- **Not the vanilla godmode** — the engine's `EntityPlayer.IsGodMode` cvar is client-authoritative and gets reset by `NetPackageEntityAliveFlags` sync from clients. Styx's intended approach is its own protected-entity registry that survives client sync.
- **Intentionally does not prevent broken legs** even when working — the broken-leg buff add path runs separately from damage. Pair with `buffDontBreakMyLeg` if you want fall protection (see `STYX_CAPABILITIES.md §13`).
- **No reliable visual indicator** — godmode (when working) is silent. There is no built-in query command.
