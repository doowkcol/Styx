# Plugin authoring — embedded manifests

Styx plugins are single `.cs` files that drop into `Mods/Styx/plugins/`. The framework compiles them at boot, hot-reloads them on save, and — using the **plugin manifest synthesis** system documented here — *also* extracts XML payloads embedded in the source file and writes them to the canonical 7DTD config files at boot.

Result: a plugin that needs its own buff definition, XUi panel, or localization rows can ship those alongside its C# code in **one file** the operator drops into `plugins/`. No separate XML files to merge, no operator hand-editing of `Config/buffs.xml` or `Config/XUi/windows.xml`.

This document covers the marker format, the synthesis lifecycle, every supported section, and the gotchas that bite on the way.

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

## Reference plugins

Browse these as embedded-manifest examples:

| Plugin | Sections used |
|---|---|
| `StyxNvg.cs` | `@styx-buffs` (single buff with onSelfBuffStart triggers) |
| `StyxShield.cs` | `@styx-buffs`, `@styx-xui-windows`, `@styx-xui-window-group toolbelt` (panel + buff + group reg) |
| `StyxBuffs.cs` | `@styx-buffs` (multiple buffs in one block), `@styx-xui-windows` (large picker panel), `@styx-xui-window-group toolbelt` |
| `PermEditor.cs` | `@styx-xui-windows` (multi-stage UI with sliding window), `@styx-xui-window-group toolbelt` |
| `StyxMenu.cs` | Two windows in one plugin (`styxMenu` + `styxLauncher`), each with its own group reg |
| `StyxZombieRadar.cs` | `@styx-xui-windows` only — no `@styx-xui-window-group` block, so the window is defined but not mounted (intentionally disabled by default) |

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
        // Lifecycle hooks discovered by name. No registration needed.
    }
}
```

The `/* @styx-* */` blocks get processed at framework `InitMod()` (before the engine reads XML). Then 7DTD reads the synthesised XML files. Then your plugin's `OnLoad()` runs once `OnGameStartDone` fires. By the time `OnLoad` runs, the buff/window/locale you embedded already exists in the engine.
