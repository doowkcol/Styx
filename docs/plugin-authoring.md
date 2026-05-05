# Plugin authoring

Styx plugins are single `.cs` files that drop into `Mods/Styx/plugins/`. The framework compiles them at boot and hot-reloads them on save. Two authoring concerns this document covers:

1. **Embedded manifests** — XML payloads in `/* @styx-* */` block comments. The framework extracts them at boot and writes the canonical 7DTD config files (`Config/buffs.xml`, `Config/XUi/windows.xml`, `Config/XUi/xui.xml`, `Config/Localization.txt`) so a plugin that needs its own buff definition, XUi panel, or localization rows ships everything in one file. No separate XML files to merge, no operator hand-editing.

2. **Harmony patches** — `[HarmonyPatch]`-decorated classes inside the plugin file. The framework owns the lifecycle: patches auto-apply after `OnLoad`, auto-remove before `OnUnload`, hot-reload cleanly. For named lifecycle events (`OnPlayerSpawned`, `OnEntityDeath`, `OnPlayerDamage`, ~30 others) the framework already ships first-party hooks and you don't need to write patches at all — but for everything else, plugin-author Harmony is supported.

---

## How synthesis works

At `InitMod()` — *before* the engine reads any XML configs — Styx walks `Mods/Styx/plugins/*.cs` reading each file as text. Block comments matching `/* @styx-<section> [arg] ... */` are extracted, concatenated, and written to the canonical 7DTD config files inside `Mods/Styx/Config/`:

| Section | Output file | xpath wrapper |
|---|---|---|
| `@styx-buffs` | `Config/buffs.xml` | `<append xpath="/buffs">` |
| `@styx-xui-windows` | `Config/XUi/windows.xml` | `<append xpath="/windows">` |
| `@styx-xui-window-group <group>` | `Config/XUi/xui.xml` | `<append xpath="/xui/ruleset[@name='default']/window_group[@name='<group>']">` |
| `@styx-localization` | `Config/Localization.txt` | (raw rows, no wrapper) |

The framework owns those four files outright. Operator-authored content for those file paths inside `Mods/Styx/Config/` will be **overwritten on next boot.** Operators with server-specific custom content should ship it in their own mod folder (`Mods/MyServerExtras/Config/buffs.xml` etc.) — 7DTD's modlet system merges patches from every mod, so operator and plugin content coexist.

**Skip-when-empty:** if no plugin contributes blocks for a given section, the framework does **not** touch that section's target file. Lets you migrate one section type at a time without surprises.

**Idempotent:** content is built in memory then compared against the on-disk file. The framework only writes when content actually changes — no spurious mtime churn.

---

## Marker syntax

```csharp
/* @styx-<section> [optional-argument]
<XML / text body>
*/
```

- The opening `/*` and the section tag must be on the **same line**.
- `<section>` is one of `buffs`, `xui-windows`, `xui-window-group`, `localization`.
- An optional same-line argument follows the tag (used by `xui-window-group` to name the target group, e.g. `toolbelt`).
- The body starts on the next line and runs to `*/`.
- The body is extracted verbatim; the framework wraps it in the appropriate xpath patch element when generating the output file.

Place blocks anywhere in the `.cs` file. Convention is **after** the `using` directives and **before** the `[Info(...)]` attribute, like this:

```csharp
using System;
using Styx;
using Styx.Plugins;

/* @styx-buffs
<buff name="buffMyPlugin" ...>
    ...
</buff>
*/

/* @styx-xui-windows
<window name="myPlugin" ...>
    ...
</window>
*/

/* @styx-xui-window-group toolbelt
<window name="myPlugin" />
*/

[Info("MyPlugin", "Author", "1.0.0")]
public class MyPlugin : StyxPlugin { /* ... */ }
```

Multiple blocks of the same type are fine. Each is concatenated into the output file with a source marker comment so you can trace generated content back to its plugin.

---

## Section reference

### `@styx-buffs`

Buff definitions. Body is one or more `<buff>...</buff>` elements. The framework concatenates them inside `<append xpath="/buffs">` in the synthesised `Config/buffs.xml`.

```csharp
/* @styx-buffs
<buff name="buffMyPlugin"
      name_key="buffMyPluginName"
      description_key="buffMyPluginDesc"
      icon="ui_game_symbol_lightbulb">
    <stack_type value="replace"/>
    <duration value="999999"/>
    <effect_group>
        <triggered_effect trigger="onSelfBuffStart" action="..."/>
    </effect_group>
</buff>
*/
```

Then apply the buff at runtime via `StyxCore.Player.ApplyBuff(player, "buffMyPlugin", duration)`.

### `@styx-xui-windows`

XUi window definitions. Body is one or more `<window name="...">...</window>` elements. Framework wraps them in `<append xpath="/windows">`.

```csharp
/* @styx-xui-windows
<window name="myPlugin"
        anchor="CenterCenter" pos="-260,170"
        width="520" height="340"
        pivot="TopLeft"
        controller="ToolbeltWindow"
        depth="52">
    <rect name="wrap" pos="0,0" width="520" height="340"
          visible="{#cvar('myPlugin.open') == 1}">
        <!-- panel contents -->
    </rect>
</window>
*/
```

Window definitions only declare structure. Driving the panel with cvars, registering handlers, etc. is plugin runtime work — see `StyxBuffs.cs`, `PermEditor.cs`, or `StyxShield.cs` for examples.

### `@styx-xui-window-group <group>`

Window-group registration. Tells the engine to mount the named window inside a specific UI group at boot. `<group>` is the same-line argument; `toolbelt` is the most common (always-mounted alongside the HUD).

```csharp
/* @styx-xui-window-group toolbelt
<window name="myPlugin" />
*/
```

Pair this with a `@styx-xui-windows` block of the same window name. If you skip this block, the window is defined but never mounted (useful for windows you want operators to opt into via separate config).

### `@styx-localization`

Plain-text localization rows in 7DTD's `Key,Source,Type,English[,others]` format. Body is appended verbatim to `Config/Localization.txt`.

```csharp
/* @styx-localization
buffMyPluginName,buffs,Buff,My Plugin Buff
buffMyPluginDesc,buffs,Buff,"Description with comma — quoted to escape it"
*/
```

**Most plugins don't need this section.** Use `Styx.Ui.Labels.Register(this, key, value)` from `OnLoad` instead — runtime registration injects into the engine's localization table via the framework's `EngineBridge`, so values are available without operator file edits and survive plugin hot-reload. The file-based path is for when you specifically want labels baked into client localization at first connect (rare).

---

## Toggle

`configs/StyxFramework.json` controls synthesis at the framework level:

```json
{
  "PluginManifestSynthesis": {
    "Enabled": true
  }
}
```

| Setting | Behaviour |
|---|---|
| `Enabled: true` (default) | Each boot, framework rebuilds the four output files from current plugin blocks. |
| `Enabled: false` | Synthesis is a complete no-op for the boot. Hand-edit any of the framework-owned files for live iteration; your edits stay until you flip back to `true`. |

The toggle exists for **plugin development**, not for operators. Typical workflow:

1. Develop with the toggle on. Plugin block changes flow through synthesis at next boot.
2. To hot-iterate on the generated `buffs.xml` without recompiling the plugin, flip the toggle to `false`, edit the file, restart, test.
3. When the change works, port it back into the plugin's `/* @styx-buffs */` block, flip the toggle back to `true`, restart. Synthesis regenerates the same content.

---

## Common gotchas

### `--` inside XML comments

XML spec forbids `--` inside `<!-- ... -->`. 7DTD's parser is strict about this — a single `Foo -- bar` comment makes the engine silently reject the entire file. **The synthesiser auto-rewrites `--` to em-dash inside comment bodies** and warns in the synthesis report, so plugin authors usually don't hit this in practice. But if you're debugging "my buff didn't load," check the embedded XML for `--` in `<!-- ... -->` first.

### Icon names that don't exist

Buff icons must reference real sprites in 7DTD's atlas. `ui_game_symbol_shield` and `ui_game_symbol_fist` *don't exist* even though the names sound vanilla — when an icon name fails to resolve, the engine renders an empty slot in the buff bar. Verify your chosen icon exists in vanilla `Data/Config/buffs.xml` before committing. Common safe icons: `ui_game_symbol_zombie`, `ui_game_symbol_defense`, `ui_game_symbol_armor_iron`, `ui_game_symbol_lightbulb`.

### Buff `name_key` / `description_key` resolution

Localization keys referenced from buff XML resolve at HUD render time on the client. The framework's `Ui.Labels.Register` injects values into the server's table and pushes them to clients on connect — already-connected players need to relog once after a plugin first loads to see new labels.

For framework-shipped buffs the convention is: register the labels via `Ui.Labels.Register` in `OnLoad` (works for hot-reload, picks up at connect), AND/OR include them as `@styx-localization` rows for first-connect availability without runtime registration.

### `controller="ToolbeltWindow"` on custom windows

Most server-driven custom panels work with the `ToolbeltWindow` controller. Don't reference custom controllers unless you've also registered the controller class — XUi will throw at boot.

### Operator `Mods/Styx/Config/*` files

These are framework-owned when synthesis is enabled. Treat them as generated artefacts — don't hand-edit, don't commit live changes from your test server. If you have server-specific custom content, ship it in your **own** mod folder under `Mods/<your-name>/Config/...`.

---

## Harmony patches

Beyond the named lifecycle hooks the framework already exposes (`OnPlayerSpawned`, `OnEntityDeath`, `OnPlayerDamage`, `OnLootContainerOpened`, `OnPlayerLevelUp`, `OnBlockPlaced`, `OnVehicleMount`, ~30 others — see `src/Styx.Core/Hooks/FirstPartyPatches.cs`), plugins can patch any vanilla method directly using `[HarmonyPatch]`. The framework owns the lifecycle: patches auto-apply after `OnLoad()` and auto-remove before `OnUnload()`, so hot-reload comes off cleanly.

**Use Harmony when:** you need a vanilla method no named hook covers — connection gating, block-placement filtering, tile-entity tick postfixes, custom packet interception, anything where a method-level cut is the right tool.

**Don't use Harmony when:** a named hook already exists. If you want `OnEntityDeath`, just declare `void OnEntityDeath(EntityAlive victim)` on your plugin class — the framework's first-party patch fires it for you. Named hooks are cheaper, idiomatic, and survive vanilla-method-signature drift across V2.6 point releases better than rolling your own patch.

### The pattern

`[HarmonyPatch]`-decorated nested static class anywhere in the file. The framework calls `harmony.PatchAll(yourAssembly)` automatically — no imperative wiring in `OnLoad`.

```csharp
using HarmonyLib;
using Styx;
using Styx.Plugins;

[Info("MyPlugin", "Author", "1.0.0")]
public class MyPlugin : StyxPlugin
{
    // Patch classes are static — they can't capture the plugin instance.
    // Mirror what they need into a static pointer, set in OnLoad, cleared
    // in OnUnload.
    internal static MyPlugin Current { get; private set; }
    private bool _logDrops;

    public override void OnLoad()    { Current = this; _logDrops = true; }
    public override void OnUnload()  { Current = null; }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.ItemDropServer),
        new[] { typeof(ItemStack), typeof(UnityEngine.Vector3), typeof(UnityEngine.Vector3),
                typeof(int), typeof(float), typeof(bool) })]
    static class ItemDropServerProbe
    {
        static void Prefix(ItemStack _itemStack, int _entityId)
        {
            if (Current == null || !Current._logDrops) return;
            Log.Out("[MyPlugin] Drop: itemType={0} to entityId {1}",
                _itemStack?.itemValue?.type ?? -1, _entityId);
        }
    }
}
```

Verbatim shape from `HelloSource.cs` — the shipped probe-as-demo. Read it as the canonical hello-world.

### Lifecycle

| Plugin event | Harmony action | Source |
|---|---|---|
| Plugin loads | After `OnLoad()` returns, framework calls `harmony.PatchAll(plugin assembly)` under Harmony id `styx.plugin.<name-lowercase>` | `PluginLoader.cs` `LoadAssemblyBytes` |
| Plugin unloads (or hot-reload) | Before `OnUnload()` runs, framework calls `harmony.UnpatchSelf()` for that plugin's id, removing only its own patches | `PluginLoader.cs` `UnloadFile` |
| Save the .cs file | Roslyn recompiles → old plugin unloads (patches off) → new plugin loads (patches on) | Same lifecycle, twice |

Implementation: `src/Styx.Core/Patching/HarmonyPatchManager.cs`. Read it once if you want to know exactly what runs when.

### Inspecting active patches

```
styx patches
```

Lists framework-owned patches (Harmony id `styx.core`) plus every loaded plugin's patches, grouped by id with patch count. Useful for confirming hot-reload removed your patches cleanly, or spotting cross-plugin patch pile-up on the same target.

### `HarmonyLib` reference path

`using HarmonyLib;` works without any project setup. The plugin compiler scans sibling mod folders at boot and picks up `0Harmony.dll` from `0_TFP_Harmony` (vanilla 7DTD ships Harmony) — already on the compile reference path. See `PluginCompiler.cs` `BuildReferences` for the scan order.

### Cancellable damage hooks

Three of the framework's damage-related hooks are cancellable — return non-null from your handler:

| Hook | Return | Effect |
|---|---|---|
| `OnEntityDamage(EntityAlive victim, DamageSource source, int strength, bool critical)` | `null` | pass through (default) |
| `OnEntityDamage` / `OnPlayerDamage` | `false` | cancel damage entirely |
| `OnEntityDamage` / `OnPlayerDamage` | `int N`, `N > 0` | override damage to N |
| `OnEntityDamage` / `OnPlayerDamage` | `int N`, `N <= 0` | cancel damage |
| `OnPreDamageApplied(EntityAlive victim, DamageResponse response)` | non-null | cancel HP deduction |

Semantics defined in `Styx.Hooks.FirstParty.DamageHookHelpers`. For damage-output anti-cheat (catch a player dealing impossibly-large damage), patch `EntityAlive.OnEntityDeath` and inspect the killing-blow `DamageResponse.Strength` directly — that pattern doesn't fit the cancellable hook shape because by then the entity is already dying, and you want to inspect the strike that killed it.

### Gotchas

- **Vanilla method signature drift.** A V2.6 point release can rename a method or change its parameter list. Patches fail at `harmony.PatchAll` time; the framework logs `Harmony PatchAll failed for <plugin>: ...`, calls `UnpatchSelf` to roll back, and continues loading the plugin without patches. Watch for that line after engine updates.
- **`PatchAll` is all-or-nothing per plugin.** If one `[HarmonyPatch]` class fails (target method gone), the whole batch for that plugin is rolled back via `UnpatchSelf` and the plugin runs without any patches. Split brittle patches across multiple plugins if you need partial-success behaviour.
- **Patch classes are static — don't capture the plugin instance.** Mirror what the patch needs into a static `Current` property set in `OnLoad` and cleared in `OnUnload` (see the example above and `HelloSource.cs`).
- **Don't bind to or remove framework patches.** Anything under Harmony id `styx.core` is framework-private API. Patches living there can shift between framework versions. If a named hook exists for what you want, use the hook.
- **Multiple plugins patching the same target.** Harmony runs prefixes / postfixes in priority order (default `Priority.Normal`). When ordering matters use `[HarmonyPriority]`. When it doesn't, don't — gratuitous priority annotations make the patch graph harder to reason about. Use `styx patches` to see who's patching what.
- **Roslyn happily compiles a wrong target.** A typo in a method name `nameof(...)` resolves locally but fails at `PatchAll` time as a runtime log line. Test the round-trip after every patch edit.

---

## Reference plugins

Browse these as authoring examples:

| Plugin | What it shows |
|---|---|
| `StyxNvg.cs` | Embedded manifest: `@styx-buffs` (single buff with onSelfBuffStart triggers) |
| `StyxShield.cs` | Embedded manifest: `@styx-buffs`, `@styx-xui-windows`, `@styx-xui-window-group toolbelt` (panel + buff + group reg) |
| `StyxBuffs.cs` | Embedded manifest: `@styx-buffs` (multiple buffs in one block), `@styx-xui-windows` (large picker panel), `@styx-xui-window-group toolbelt` |
| `PermEditor.cs` | Embedded manifest: `@styx-xui-windows` (multi-stage UI with sliding window), `@styx-xui-window-group toolbelt` |
| `StyxMenu.cs` | Embedded manifest: two windows in one plugin (`styxMenu` + `styxLauncher`), each with its own group reg |
| `StyxZombieRadar.cs` | Embedded manifest: `@styx-xui-windows` only — no `@styx-xui-window-group` block, so the window is defined but not mounted (intentionally disabled by default) |
| `HelloSource.cs` | Harmony: single-method probe on `GameManager.ItemDropServer`. Demonstrates the static-`Current`-pointer pattern and the hot-reload round-trip |
| `StyxCrafting.cs` | Harmony: four substantial patches across `EffectManager.GetValue`, `TileEntityWorkstation.read` / `.HandleRecipeQueue` / `.UpdateTick` for perm-tiered craft-time / output-multiplier / auto-shutdown behaviour. Real-world reference for non-trivial patch authoring |

---

## Plugin lifecycle (recap)

What you write in `MyPlugin.cs`:

```csharp
[Info("MyPlugin", "Author", "1.0.0")]
public class MyPlugin : StyxPlugin
{
    public override void OnLoad()
    {
        // Runtime wire-up: commands, hooks, perms, scheduler timers, etc.
    }

    public override void OnUnload()
    {
        // Cleanup. The framework calls this on hot-reload too.
    }

    void OnPlayerSpawned(ClientInfo ci, RespawnType reason, Vector3i pos)
    {
        // Named lifecycle hooks discovered by method name. No registration needed.
    }

    [HarmonyPatch(typeof(SomeGameClass), nameof(SomeGameClass.SomeMethod))]
    static class SomeGameClass_SomeMethod_Patch
    {
        // [HarmonyPatch] classes auto-applied by the framework after OnLoad
        // and removed before OnUnload. See the Harmony patches section above.
    }
}
```

Boot order:

1. Framework `InitMod()` runs the manifest synthesis pass — `/* @styx-* */` blocks are extracted from every plugin file and written to `Config/buffs.xml` / `Config/XUi/windows.xml` / `Config/XUi/xui.xml` / `Config/Localization.txt` *before* the engine reads them.
2. 7DTD reads the synthesised XML.
3. Once `OnGameStartDone` fires, the framework loads each plugin: instantiates it, scans for named-hook methods, calls `OnLoad()`, then calls `harmony.PatchAll(plugin assembly)` to apply any `[HarmonyPatch]` classes.
4. By the time `OnLoad` runs, your embedded buff/window/locale already exists in the engine. By the time your patches apply, `OnLoad` has already run and your `Current = this;` static pointer is set.

On unload (manual unload, hot-reload, or shutdown):

1. Framework calls `harmony.UnpatchSelf()` for that plugin's id — patches off, vanilla behaviour restored.
2. `OnUnload()` runs — your cleanup code sees the world without your patches.
3. Framework unbinds named hooks, unregisters commands, flushes the plugin's data stores.
