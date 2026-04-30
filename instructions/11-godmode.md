# 11 — Godmode

> **⚠️ EXPERIMENTAL — NOT WORKING YET.**
>
> The framework patches and the plugin command surface are in place, but
> server-side godmode does not currently produce the desired effect on a
> live dedicated server. The player still appears to die from the client's
> point of view (death animation plays, respawn screen shown). See
> `STYX_V26_ENGINE_SURFACE.md §1.2` for why dedicated-server damage
> cancellation is fundamentally hard, and `§1.7` / `§1.8` for the
> patches that have been tried so far.
>
> Treat this plugin as a work-in-progress. The doc below describes the
> intended surface; do not rely on it in production.

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
