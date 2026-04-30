# 22 — Hello + HelloSource (dev demos)

Minimal "hello world" demos — one as a precompiled DLL, one as a `.cs` source plugin. Reference implementations for plugin authors. Safe to delete on production.

**Operator note:** These are pure demos. They don't add any user-facing feature. Delete `Hello.dll/.pdb` and `HelloSource.cs` if you want a cleaner plugin folder.

## Hello — precompiled DLL plugin

Lives in `Mods/Styx/plugins/Styx.Hello.dll` (+ `.pdb`). Compiled in `src/Styx.Hello/` then dropped into the plugins folder.

### Commands

| Command | Perm | What |
|---|---|---|
| `/hello` | `styx.hello.use` | Whispers a greeting back to you |

### Why it exists

Demonstrates the **precompiled-DLL plugin path**. The framework supports two plugin types:
- `.cs` source files → Roslyn compiles at boot, hot-reload on save
- `.dll` precompiled assemblies → loaded as-is, no hot-reload

Most Styx plugins use the `.cs` path (faster iteration). Use the `.dll` path when:
- Plugin has multiple files (multi-class plugins)
- You want to keep source private (commercial plugin)
- Plugin uses unusual references that aren't in the framework's compile path

## HelloSource — `.cs` source plugin

Lives in `Mods/Styx/plugins/HelloSource.cs`.

### Commands

| Command | Perm | What |
|---|---|---|
| `/src` | (open) | Whispers info about the source-plugin path + lifecycle |

### Hooks demonstrated

This plugin auto-wires every hook by name:

- `OnServerInitialized()` — fires after all plugins loaded
- `OnPlayerLogin(ClientInfo)` — fires when a player connects
- `OnPlayerSpawned(...)` — fires when player spawns into world
- `OnPlayerDisconnected(...)` — fires when player leaves
- `OnChatMessage(ClientInfo, string, EChatType)` — every chat message
- `OnEntityDeath(EntityAlive)` — every entity death
- `OnPreDamageApplied(victim, response)` — universal damage choke point

Each hook just logs to `[HelloSource] …` so you can watch the framework's hook bus fire in the server log.

### Harmony patch demonstrated

```csharp
[HarmonyPatch(typeof(GameManager), nameof(GameManager.ItemDropServer))]
```

A non-invasive Postfix on `GameManager.ItemDropServer` that just logs every dropped item. Demonstrates plugin-side `[HarmonyPatch]` classes — the framework auto-discovers them at plugin load, applies them under a plugin-scoped Harmony id, and auto-unpatches on unload.

### Why it exists

Demonstrates **everything a Styx plugin can do**:
- Config (`configs/HelloSource.json`, live-reloadable)
- Commands
- Hook methods auto-wired by name
- Custom hook firing (other plugins like Kit do this too — `OnKitRedeemed`)
- Plugin-side Harmony patches

Read `HelloSource.cs` source if you're starting your own plugin.

## Permissions summary

| Perm | What |
|---|---|
| `styx.hello.use` | Run `/hello` |

`/src` is open (no perm).

## Common ops

### Delete both demos

```
rm Mods/Styx/plugins/Styx.Hello.dll
rm Mods/Styx/plugins/Styx.Hello.pdb
rm Mods/Styx/plugins/HelloSource.cs
```

Both hot-unload immediately. The chat commands disappear next time someone tries them.

### Use HelloSource as a template for your own plugin

1. Copy `HelloSource.cs` to `MyPlugin.cs` in `Mods/Styx/plugins/`
2. Rename the class + `[Info("MyPlugin", ...)]` attribute
3. Edit hooks / commands to taste
4. Save → Roslyn compiles → hot-loads

The framework discovers it by class name + `[Info]` attribute. No registration needed.

## See also

- `STYX_CAPABILITIES.md` — full plugin-authoring API menu
- `STYX_HOOK_CATALOGUE.md` — every hook the framework dispatches
