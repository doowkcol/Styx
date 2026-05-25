// SPDX-License-Identifier: LicenseRef-Styx-Plugin-Restricted
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood). All rights reserved.
//
// Darkness Stumbles -- DS:Classes (v0.1 scaffold).
// Internal proof-of-concept for class-based progression mirroring DF's
// roster (Farmer / Hunter / Laborer / Mechanic / Soldier / Survivalist
// / Security / Civilian). Validates that class-tier perm emission +
// signature buffs + per-player data store all hang together cleanly
// on top of Styx primitives.
//
// What this scaffold does:
//   - Registers 8 classes with display names, descriptions, signature
//     buff names, and emitted perm flags.
//   - Ships 8 signature buffs via @styx-buffs synthesis -- each a
//     small set of passive_effect modifiers giving the class a
//     mechanically distinct feel (Farmer = harvest yield, Hunter =
//     ranged damage, Laborer = block damage, etc.).
//   - Per-player JSON data store at data/DsClasses/<platformId>.json
//     records the assigned class. Whole-document-snapshot, matches
//     StyxBackpack's persistence pattern.
//   - On every player spawn, applies the assigned class's signature
//     buff + grants the corresponding perm flag in the Styx perm
//     system. Other DS plugins can then gate behaviour on
//     `StyxCore.Perms.HasPermission(pid, "ds.class.farmer")`.
//   - Chat commands: /class (list, info, pick) for players to pick.
//     /dsclass for admins to set, clear, list, inspect assignments.
//   - Custom hook OnDsClassChanged(player, oldKey, newKey) so other
//     plugins can react when a class is assigned or changed.
//
// Deferred to later iterations:
//   - Menu UI for class picker (chat-based for now -- works on console
//     where typing /class pick farmer is the path of least resistance)
//   - First-spawn forced selection (currently a prompt-on-spawn message
//     if no class is assigned; player can ignore until convenient)
//   - Class change cooldowns / wipe-locking
//   - Class quests
//   - Localization rows (English inline for now; can move to
//     Ui.Labels.Register or @styx-localization later)
//
// Test plan:
//   1. /perm grant user <yourId> dsclass.admin
//   2. /class list                     -- see all 8 classes
//   3. /class info farmer              -- see effects
//   4. /class pick farmer              -- assign yourself
//   5. /class                          -- shows current class
//   6. /styx perms                     -- ds.class.farmer should be granted
//   7. Reconnect -- buff should reapply on spawn
//   8. /dsclass set <yourId> hunter    -- admin reassign (revokes farmer perm,
//                                         grants hunter perm, swaps buff)
//   9. /dsclass clear <yourId>         -- clears assignment, revokes perm
//  10. /dsclass list                   -- all current assignments
//  11. /dsclass stats                  -- diagnostic counts

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Styx;
using Styx.Commands;
using Styx.Plugins;
using Styx.Scheduling;
using UnityEngine;

/* @styx-buffs
<!--
    DS:Classes signature buffs. One per class, applied on player spawn
    when the class is assigned. duration=0 = permanent. hidden=true so
    they don't clutter the player's buff bar (effects still apply via
    passive_effect entries).
    Effect tuning is intentionally modest in the v0.1 scaffold — we
    can amplify later once the framework is proven and we've watched
    real play feel. PassiveEffects names are the engine's enum values;
    the engine silently skips unknown names so future-version drift is
    cosmetic rather than crash-causing.
-->

<buff name="buffDsClassFarmer" hidden="true">
    <stack_type value="ignore"/>
    <duration value="0"/>
    <effect_group>
        <passive_effect name="HarvestCount" operation="perc_add" value="0.20"/>
        <passive_effect name="CraftingTime" operation="perc_subtract" value="0.15" tags="food,medical"/>
    </effect_group>
</buff>

<buff name="buffDsClassHunter" hidden="true">
    <stack_type value="ignore"/>
    <duration value="0"/>
    <effect_group>
        <passive_effect name="EntityDamage" operation="perc_add" value="0.15" tags="ranged"/>
        <passive_effect name="StaminaGain" operation="perc_add" value="0.15"/>
    </effect_group>
</buff>

<buff name="buffDsClassLaborer" hidden="true">
    <stack_type value="ignore"/>
    <duration value="0"/>
    <effect_group>
        <passive_effect name="BlockDamage" operation="perc_add" value="0.25"/>
        <passive_effect name="HarvestCount" operation="perc_add" value="0.15" tags="ore,wood"/>
    </effect_group>
</buff>

<buff name="buffDsClassMechanic" hidden="true">
    <stack_type value="ignore"/>
    <duration value="0"/>
    <effect_group>
        <passive_effect name="CraftingTime" operation="perc_subtract" value="0.20" tags="vehicles,electrical,tools"/>
        <passive_effect name="CraftingTier" operation="base_add" value="1"/>
    </effect_group>
</buff>

<buff name="buffDsClassSoldier" hidden="true">
    <stack_type value="ignore"/>
    <duration value="0"/>
    <effect_group>
        <passive_effect name="EntityDamage" operation="perc_add" value="0.10"/>
        <passive_effect name="CarryCapacity" operation="base_add" value="10"/>
    </effect_group>
</buff>

<buff name="buffDsClassSurvivalist" hidden="true">
    <stack_type value="ignore"/>
    <duration value="0"/>
    <effect_group>
        <passive_effect name="StaminaGain" operation="perc_add" value="0.30"/>
        <passive_effect name="HealthGain" operation="perc_add" value="0.20"/>
    </effect_group>
</buff>

<buff name="buffDsClassSecurity" hidden="true">
    <stack_type value="ignore"/>
    <duration value="0"/>
    <effect_group>
        <passive_effect name="PhysicalDamageResist" operation="perc_add" value="0.15"/>
        <passive_effect name="HealthGain" operation="perc_add" value="0.10"/>
    </effect_group>
</buff>

<buff name="buffDsClassCivilian" hidden="true">
    <stack_type value="ignore"/>
    <duration value="0"/>
    <effect_group>
        <passive_effect name="ExperienceGain" operation="perc_add" value="0.10"/>
    </effect_group>
</buff>
*/

/* @styx-xui-windows
<!--
    DS Classes picker UI. Mounted on the toolbelt window-group so it
    renders alongside the other Styx menus. Driven by cvars set from
    the plugin's OpenMenuFor/CloseFor handlers; class names, flavor
    text and descriptions resolve via runtime-registered localization
    keys (Ui.Labels.Register at OnLoad) so the C# Classes dictionary
    is the single source of truth.

    Cvar contract:
      styx.dsclass.open    1 = visible, 0 = hidden
      styx.dsclass.sel     0..7 = which row the cursor is on
      styx.dsclass.curId   0 = unassigned, 1..8 = currently-assigned class id

    Class id mapping (must match _orderedKeys in C#):
      1 = farmer  2 = hunter   3 = laborer    4 = mechanic
      5 = soldier 6 = survivalist  7 = security  8 = civilian
-->
<window name="styxClass"
        anchor="CenterCenter" pos="-320,310"
        width="640" height="620"
        pivot="TopLeft"
        controller="ToolbeltWindow"
        depth="52">
    <rect name="wrap" pos="0,0" width="640" height="620"
          visible="{#cvar('styx.dsclass.open') == 1}">

        <!-- Background panel + border. menu_empty + menu_empty3px is the
             working two-sprite pattern used by StyxBuffs / StyxTeleport /
             StyxMenu. type="sliced" enables 9-slice scaling so the sprite
             stretches to the declared width/height. Border has
             fillcenter="false" so only the outline draws. -->
        <sprite depth="0" name="bg"     sprite="menu_empty"    color="0,0,0,220"
                type="sliced" width="640" height="620"/>
        <sprite depth="1" name="border" sprite="menu_empty3px" color="255,200,100,210"
                type="sliced" width="640" height="620" fillcenter="false"/>

        <!-- Title. font_size 26 matches StyxBuffs / StyxBuilder header
             convention; outline style for legibility against a busy
             world background, especially on console TVs at viewing
             distance. -->
        <label depth="2" name="title" text="DS CLASSES"
               font_size="26" pos="0,-20" width="640" height="32"
               justify="center" style="outline" color="255,200,100,255"/>

        <label depth="2" name="cur"
               text="{#localization('dsclass_curname_' + int(cvar('styx.dsclass.curId')))}"
               font_size="17" pos="24,-66" width="600" height="24"
               color="200,220,255,255"/>

        <sprite depth="2" name="div1" pos="24,-100" width="592" height="2" color="100,100,100,220"/>

        <!-- 8 class rows. 28px row pitch with font_size 18 for the name
             leaves enough vertical breathing room. Cursor uses font 22
             matching the established StyxBuffs/StyxBuilder cursor size.
             Row Y positions: -120, -148, -176, -204, -232, -260, -288, -316. -->

        <!-- Row 0 = Farmer (id 1) -->
        <label depth="3" name="c0" text="&gt;"
               visible="{#cvar('styx.dsclass.sel') == 0}"
               font_size="22" pos="26,-120" width="22" height="26"
               color="120,200,255,255"/>
        <label depth="3" name="r0name" text="{#localization('dsclass_name_1')}"
               font_size="18" pos="60,-120" width="150" height="26"
               color="240,240,240,255"/>
        <label depth="3" name="r0flav" text="{#localization('dsclass_flavor_1')}"
               font_size="14" pos="210,-120" width="410" height="26"
               color="180,180,180,255"/>

        <!-- Row 1 = Hunter (id 2) -->
        <label depth="3" name="c1" text="&gt;"
               visible="{#cvar('styx.dsclass.sel') == 1}"
               font_size="22" pos="26,-148" width="22" height="26"
               color="120,200,255,255"/>
        <label depth="3" name="r1name" text="{#localization('dsclass_name_2')}"
               font_size="18" pos="60,-148" width="150" height="26"
               color="240,240,240,255"/>
        <label depth="3" name="r1flav" text="{#localization('dsclass_flavor_2')}"
               font_size="14" pos="210,-148" width="410" height="26"
               color="180,180,180,255"/>

        <!-- Row 2 = Laborer (id 3) -->
        <label depth="3" name="c2" text="&gt;"
               visible="{#cvar('styx.dsclass.sel') == 2}"
               font_size="22" pos="26,-176" width="22" height="26"
               color="120,200,255,255"/>
        <label depth="3" name="r2name" text="{#localization('dsclass_name_3')}"
               font_size="18" pos="60,-176" width="150" height="26"
               color="240,240,240,255"/>
        <label depth="3" name="r2flav" text="{#localization('dsclass_flavor_3')}"
               font_size="14" pos="210,-176" width="410" height="26"
               color="180,180,180,255"/>

        <!-- Row 3 = Mechanic (id 4) -->
        <label depth="3" name="c3" text="&gt;"
               visible="{#cvar('styx.dsclass.sel') == 3}"
               font_size="22" pos="26,-204" width="22" height="26"
               color="120,200,255,255"/>
        <label depth="3" name="r3name" text="{#localization('dsclass_name_4')}"
               font_size="18" pos="60,-204" width="150" height="26"
               color="240,240,240,255"/>
        <label depth="3" name="r3flav" text="{#localization('dsclass_flavor_4')}"
               font_size="14" pos="210,-204" width="410" height="26"
               color="180,180,180,255"/>

        <!-- Row 4 = Soldier (id 5) -->
        <label depth="3" name="c4" text="&gt;"
               visible="{#cvar('styx.dsclass.sel') == 4}"
               font_size="22" pos="26,-232" width="22" height="26"
               color="120,200,255,255"/>
        <label depth="3" name="r4name" text="{#localization('dsclass_name_5')}"
               font_size="18" pos="60,-232" width="150" height="26"
               color="240,240,240,255"/>
        <label depth="3" name="r4flav" text="{#localization('dsclass_flavor_5')}"
               font_size="14" pos="210,-232" width="410" height="26"
               color="180,180,180,255"/>

        <!-- Row 5 = Survivalist (id 6) -->
        <label depth="3" name="c5" text="&gt;"
               visible="{#cvar('styx.dsclass.sel') == 5}"
               font_size="22" pos="26,-260" width="22" height="26"
               color="120,200,255,255"/>
        <label depth="3" name="r5name" text="{#localization('dsclass_name_6')}"
               font_size="18" pos="60,-260" width="150" height="26"
               color="240,240,240,255"/>
        <label depth="3" name="r5flav" text="{#localization('dsclass_flavor_6')}"
               font_size="14" pos="210,-260" width="410" height="26"
               color="180,180,180,255"/>

        <!-- Row 6 = Security (id 7) -->
        <label depth="3" name="c6" text="&gt;"
               visible="{#cvar('styx.dsclass.sel') == 6}"
               font_size="22" pos="26,-288" width="22" height="26"
               color="120,200,255,255"/>
        <label depth="3" name="r6name" text="{#localization('dsclass_name_7')}"
               font_size="18" pos="60,-288" width="150" height="26"
               color="240,240,240,255"/>
        <label depth="3" name="r6flav" text="{#localization('dsclass_flavor_7')}"
               font_size="14" pos="210,-288" width="410" height="26"
               color="180,180,180,255"/>

        <!-- Row 7 = Civilian (id 8) -->
        <label depth="3" name="c7" text="&gt;"
               visible="{#cvar('styx.dsclass.sel') == 7}"
               font_size="22" pos="26,-316" width="22" height="26"
               color="120,200,255,255"/>
        <label depth="3" name="r7name" text="{#localization('dsclass_name_8')}"
               font_size="18" pos="60,-316" width="150" height="26"
               color="240,240,240,255"/>
        <label depth="3" name="r7flav" text="{#localization('dsclass_flavor_8')}"
               font_size="14" pos="210,-316" width="410" height="26"
               color="180,180,180,255"/>

        <sprite depth="2" name="div2" pos="24,-352" width="592" height="2" color="100,100,100,220"/>

        <label depth="2" name="effect_label" text="Effects:"
               font_size="15" pos="24,-368" width="100" height="22"
               color="180,180,180,255"/>

        <!-- Description for the highlighted class. Looked up by sel.
             dsclass_desc_1..8 are registered at OnLoad. Generous height
             (110px) for multi-line wrap on the longer descriptions
             (Mechanic / Civilian) without clipping. -->
        <label depth="2" name="desc"
               text="{#localization('dsclass_desc_' + (int(cvar('styx.dsclass.sel')) + 1))}"
               font_size="16" pos="24,-396" width="592" height="110"
               color="200,220,200,255"/>

        <label depth="2" name="hint" text="[SCROLL] navigate    [LMB] pick    [RMB] back"
               font_size="14" pos="0,-588" width="640" height="22"
               justify="center" color="160,160,160,255"/>
    </rect>
</window>
*/

/* @styx-xui-window-group toolbelt
<window name="styxClass"/>
*/

[Info("DsClasses", "Doowkcol", "0.1.0")]
public class DsClasses : StyxPlugin
{
    public override string Description => "DS:Classes -- DF-style class-based progression scaffold (internal).";

    // ============================================================ class registry

    /// <summary>One row in the class registry. Hardcoded for v0.1; could
    /// become file-driven later (configs/DsClasses.json) if classes
    /// need to be tunable without recompiling.</summary>
    private sealed class ClassDef
    {
        public string Key;          // "farmer" -- case-insensitive lookup key
        public string DisplayName;  // "Farmer"
        public string Description;  // shown by /class info
        public string BuffName;     // "buffDsClassFarmer" -- must match @styx-buffs above
        public string PermFlag;     // "ds.class.farmer" -- granted/revoked on assign
        public string Flavor;       // 1-line summary shown by /class list
    }

    private static readonly Dictionary<string, ClassDef> Classes =
        new Dictionary<string, ClassDef>(StringComparer.OrdinalIgnoreCase)
    {
        ["farmer"] = new ClassDef
        {
            Key = "farmer",
            DisplayName = "Farmer",
            Flavor = "Green thumb. Faster food + medical crafting, +20% harvest yield.",
            Description = "+20% harvest count on plants, -15% crafting time on food and medical recipes.",
            BuffName = "buffDsClassFarmer",
            PermFlag = "ds.class.farmer",
        },
        ["hunter"] = new ClassDef
        {
            Key = "hunter",
            DisplayName = "Hunter",
            Flavor = "Ranged specialist. +15% ranged damage, +15% stamina gain.",
            Description = "+15% damage with ranged weapons, +15% stamina gain rate.",
            BuffName = "buffDsClassHunter",
            PermFlag = "ds.class.hunter",
        },
        ["laborer"] = new ClassDef
        {
            Key = "laborer",
            DisplayName = "Laborer",
            Flavor = "Pick swinger. +25% block damage, +15% ore/wood harvest.",
            Description = "+25% block damage, +15% harvest count on ore and wood.",
            BuffName = "buffDsClassLaborer",
            PermFlag = "ds.class.laborer",
        },
        ["mechanic"] = new ClassDef
        {
            Key = "mechanic",
            DisplayName = "Mechanic",
            Flavor = "Workshop chief. -20% craft time on vehicles/electrical/tools, +1 craft tier.",
            Description = "-20% crafting time on vehicles, electrical, and tools. +1 to crafting tier (better quality items).",
            BuffName = "buffDsClassMechanic",
            PermFlag = "ds.class.mechanic",
        },
        ["soldier"] = new ClassDef
        {
            Key = "soldier",
            DisplayName = "Soldier",
            Flavor = "Combat-ready. +10% damage, +10 carry capacity.",
            Description = "+10% damage dealt across all weapons, +10 base carry capacity (can carry heavier loadouts).",
            BuffName = "buffDsClassSoldier",
            PermFlag = "ds.class.soldier",
        },
        ["survivalist"] = new ClassDef
        {
            Key = "survivalist",
            DisplayName = "Survivalist",
            Flavor = "Endurance specialist. +30% stamina gain, +20% healing.",
            Description = "+30% stamina gain rate, +20% healing rate (food, medicines, and regen ticks all amplified).",
            BuffName = "buffDsClassSurvivalist",
            PermFlag = "ds.class.survivalist",
        },
        ["security"] = new ClassDef
        {
            Key = "security",
            DisplayName = "Security",
            Flavor = "Defensive. +15% physical damage resist, +10% healing.",
            Description = "+15% physical damage resistance (incoming bullet/melee damage reduced), +10% healing rate.",
            BuffName = "buffDsClassSecurity",
            PermFlag = "ds.class.security",
        },
        ["civilian"] = new ClassDef
        {
            Key = "civilian",
            DisplayName = "Civilian",
            Flavor = "Adaptable. +10% XP gain.",
            Description = "+10% XP gain across all sources -- the generalist option for players who don't want a niche.",
            BuffName = "buffDsClassCivilian",
            PermFlag = "ds.class.civilian",
        },
    };

    // ============================================================ data store

    /// <summary>Per-player class assignment record. One file per player
    /// at data/DsClasses/{platformIdCombined}.json. Whole-document
    /// snapshot, deserialised on demand (no in-memory cache for v0.1 --
    /// can add caching if hot-path lookups demand it).</summary>
    public sealed class PlayerClassRecord
    {
        public string ClassKey;        // matches Classes dict key
        public string AssignedAtUtc;   // ISO-8601 timestamp
        public string AssignedBy;      // "self" or admin player name
    }

    private string _saveDir;

    private const string AdminPerm = "dsclass.admin";
    private const string UsePerm   = "dsclass.use";  // gates the /m -> Class entry; default-grant via perm editor

    /// <summary>
    /// Stable ordering for the 8 classes. Position in this list maps to
    /// the integer id used by the styxClass XUi window's localization
    /// keys (dsclass_name_1..8 = farmer..civilian). Don't reorder
    /// without also re-registering Ui.Labels for the new positions and
    /// understanding that any save files referencing positions (none
    /// today) would need migration.
    /// </summary>
    private static readonly string[] OrderedKeys = new[]
    {
        "farmer", "hunter", "laborer", "mechanic",
        "soldier", "survivalist", "security", "civilian",
    };

    /// <summary>Class id for cvar use. 0 = unassigned, 1..8 = OrderedKeys index + 1.</summary>
    private static int ClassIdOf(string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        int idx = Array.IndexOf(OrderedKeys, key.ToLowerInvariant());
        return idx >= 0 ? idx + 1 : 0;
    }

    /// <summary>Per-player UI-open tracker. Filtering OnPlayerInput on
    /// this set means we don't dispatch dsclass UI events to players
    /// who happen to have the input claim from another open menu.</summary>
    private readonly HashSet<int> _uiOpenFor = new HashSet<int>();

    // ============================================================ lifecycle

    public override void OnLoad()
    {
        _saveDir = Path.Combine(StyxCore.DataPath(), "DsClasses");
        Directory.CreateDirectory(_saveDir);

        StyxCore.Commands.Register("class",
            "Pick or view your DS class -- /class [list|info <name>|pick <name>]",
            HandleClassCommand);

        StyxCore.Commands.Register("dsclass",
            "DS:Classes admin -- /dsclass <set|clear|list|stats|info>",
            HandleAdminCommand);

        // Register class names, flavors, descriptions as runtime
        // localization keys. The styxClass XUi window references these
        // by id (dsclass_name_1..8 etc.). One-source-of-truth: change
        // the Classes dictionary entries and these labels update on
        // next plugin reload.
        for (int i = 0; i < OrderedKeys.Length; i++)
        {
            int id = i + 1;
            if (Classes.TryGetValue(OrderedKeys[i], out var def))
            {
                Styx.Ui.Labels.Register(this, "dsclass_name_"   + id, def.DisplayName);
                Styx.Ui.Labels.Register(this, "dsclass_flavor_" + id, def.Flavor);
                Styx.Ui.Labels.Register(this, "dsclass_desc_"   + id, def.Description);
            }
        }

        // The "Current: ..." label at the top of the window resolves
        // off curId. 0 = unassigned, 1..8 = OrderedKeys index + 1.
        Styx.Ui.Labels.Register(this, "dsclass_curname_0", "Current: [ff8888]None[-]  --  pick one below.");
        for (int i = 0; i < OrderedKeys.Length; i++)
        {
            int id = i + 1;
            if (Classes.TryGetValue(OrderedKeys[i], out var def))
            {
                Styx.Ui.Labels.Register(this, "dsclass_curname_" + id,
                    "Current: [00ff66]" + def.DisplayName + "[-]");
            }
        }

        // Mount as a /m launcher entry. Open to all players -- class
        // picking is core gameplay, every player needs the entry. The
        // UsePerm constant is kept above for operators who want to
        // gate it later (just swap permission: null for permission: UsePerm).
        Styx.Ui.Menu.Register(this, "Class  /class", OpenMenuFor, permission: null);

        // UI open-state clears on every spawn -- otherwise a server restart
        // (or disconnect) while a player had /m -> Class open would reopen
        // the panel for them on next connect, with the in-memory _uiOpenFor
        // empty so input does nothing and the UI is "stuck". The framework
        // zeroes these on OnPlayerSpawned before plugin hooks fire.
        // (STYX_CAPABILITIES.md sec 10g, shipped v0.6.3.)
        Styx.Ui.Ephemeral.Register("styx.dsclass.open", "styx.dsclass.sel", "styx.dsclass.curId");

        // Input handling: the OnPlayerInput method below is auto-bound by
        // the framework's hook bus (any method whose name starts with
        // "On" + uppercase letter gets wired by HookManager.ScanAndBind).
        // The framework's Ui.Input.Dispatch fires both Ui.Input.OnInput
        // (event) AND the "OnPlayerInput" hook (Ui.cs:342-345), so
        // subscribing explicitly with `Ui.Input.OnInput += OnPlayerInput`
        // would double-fire the handler -- one increment via the event,
        // one via the hook bus -- and scroll-sel jumps two rows per tick.
        // Established pattern across StyxBuffs etc. is hook-bus only.

        Log.Out("[DsClasses] Loaded v0.1.0 -- {0} classes registered: {1}",
            Classes.Count, string.Join(", ", Classes.Values.Select(c => c.DisplayName)));
    }

    public override void OnUnload()
    {
        // Hook-bus subscription for OnPlayerInput is auto-cleared by the
        // framework when the plugin unloads -- no manual unsub needed
        // (matches StyxBuffs etc.). Just drop the menu + labels we own.
        try { Styx.Ui.Menu.UnregisterAll(this); } catch { }
        try { Styx.Ui.Labels.UnregisterAll(this); } catch { }

        // Best-effort: hide any open menus on plugin reload so clients
        // don't have a ghost panel that no longer responds to input.
        var cm = ConnectionManager.Instance?.Clients?.list;
        if (cm != null)
        {
            foreach (var ci in cm)
            {
                try
                {
                    var entity = GameManager.Instance?.World?.GetEntity(ci.entityId) as EntityPlayer;
                    if (entity != null) Styx.Ui.SetVar(entity, "styx.dsclass.open", 0f);
                }
                catch { }
            }
        }

        _uiOpenFor.Clear();
        Log.Out("[DsClasses] Unloaded.");
    }

    // ============================================================ menu UI

    /// <summary>
    /// Called by the /m launcher when the player picks the Class entry.
    /// Sets the open cvar, primes sel and curId, applies the input
    /// claim. Closing happens via RMB (returns to launcher) or
    /// PrimaryAction-on-already-assigned (just closes).
    /// </summary>
    private void OpenMenuFor(EntityPlayer p)
    {
        if (p == null) return;
        var pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid)) return;

        var record = LoadRecord(pid);
        int curId = ClassIdOf(record?.ClassKey);

        Styx.Ui.SetVar(p, "styx.dsclass.curId", curId);
        Styx.Ui.SetVar(p, "styx.dsclass.sel", 0f);
        Styx.Ui.SetVar(p, "styx.dsclass.open", 1f);

        _uiOpenFor.Add(p.entityId);
        Styx.Ui.Input.Acquire(p, Name);
    }

    private void CloseFor(EntityPlayer p)
    {
        if (p == null) return;
        Styx.Ui.SetVar(p, "styx.dsclass.open", 0f);
        try { Styx.Ui.Input.Release(p, Name); } catch { }
        _uiOpenFor.Remove(p.entityId);
    }

    /// <summary>
    /// Input dispatch for an open dsclass menu. Scroll-wheel maps to
    /// Jump (next) / Crouch (prev) per the framework's input router.
    /// PrimaryAction picks the highlighted class (only takes effect if
    /// no class is currently assigned). SecondaryAction closes and
    /// returns to the launcher (matches StyxBuffs convention).
    /// </summary>
    private void OnPlayerInput(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (p == null || !_uiOpenFor.Contains(p.entityId)) return;
        if (p.Buffs == null) return;
        if ((int)p.Buffs.GetCustomVar("styx.dsclass.open") != 1) return;

        int sel = (int)p.Buffs.GetCustomVar("styx.dsclass.sel");
        const int rowCount = 8;

        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
                sel = (sel + 1) % rowCount;
                Styx.Ui.SetVar(p, "styx.dsclass.sel", sel);
                break;

            case Styx.Ui.StyxInputKind.Crouch:
                sel = (sel - 1 + rowCount) % rowCount;
                Styx.Ui.SetVar(p, "styx.dsclass.sel", sel);
                break;

            case Styx.Ui.StyxInputKind.PrimaryAction:
                HandleMenuPick(p, sel);
                break;

            case Styx.Ui.StyxInputKind.SecondaryAction:
                CloseFor(p);
                Scheduler.Once(0.05,
                    () => Styx.Ui.Menu.OpenLauncher?.Invoke(p),
                    name: "DsClasses.BackToLauncher");
                break;
        }
    }

    private void HandleMenuPick(EntityPlayer p, int sel)
    {
        if (p == null) return;
        if (sel < 0 || sel >= OrderedKeys.Length) return;
        var pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid)) return;

        var existing = LoadRecord(pid);
        if (existing != null)
        {
            // Already assigned -- whisper why, don't change anything.
            // Reassignment is admin-only by design.
            Styx.Server.Whisper(p,
                "[ffaa44]You already have a class:[-] " + existing.ClassKey +
                ". Ask an admin to change it via /dsclass set.");
            CloseFor(p);
            return;
        }

        string newKey = OrderedKeys[sel];
        if (AssignClass(pid, newKey, "self-via-menu"))
        {
            if (Classes.TryGetValue(newKey, out var def))
            {
                Styx.Server.Whisper(p, "[00ff66]Class set to " + def.DisplayName + ".[-] " + def.Flavor);
                // Refresh the curId cvar so the panel's "Current:" line
                // updates immediately if the player keeps the menu open.
                Styx.Ui.SetVar(p, "styx.dsclass.curId", sel + 1);
            }
        }
        else
        {
            Styx.Server.Whisper(p, "[ff6666]Failed to assign class.[-] See server log.");
        }
        CloseFor(p);
    }

    // ============================================================ spawn hook

    /// <summary>
    /// Auto-bound hook. On every player spawn, ensure the assigned
    /// class buff is applied and the perm flag is granted. Idempotent --
    /// applying a buff that's already on the entity is a no-op (the
    /// buff's stack_type=ignore handles that), and re-granting an
    /// existing perm is also a no-op in the perm system.
    ///
    /// If no class is assigned: whisper a prompt to the player so they
    /// know they need to pick. We don't force-block gameplay in v0.1 --
    /// classless players still play, just without their class buff.
    /// </summary>
    void OnPlayerSpawnedInWorld(ClientInfo ci, RespawnType reason, Vector3i pos)
    {
        if (ci == null) return;
        var pid = ci.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid)) return;

        var record = LoadRecord(pid);
        if (record != null && Classes.TryGetValue(record.ClassKey, out var def))
        {
            ApplyClassToPlayer(ci, def);
            return;
        }

        // No class -- prompt them. Whisper takes an EntityPlayer (not
        // ClientInfo), so resolve via the world. On the very first spawn
        // tick the entity may not be fully initialised; if the resolve
        // fails we just silently skip the prompt this time -- the next
        // OnPlayerSpawnedInWorld (e.g. respawn) will pick them up.
        try
        {
            var entity = GameManager.Instance?.World?.GetEntity(ci.entityId) as EntityPlayer;
            if (entity != null)
            {
                Styx.Server.Whisper(entity,
                    "[ffaa44]No DS class assigned.[-] Type [00ff66]/class list[-] " +
                    "to see options, then [00ff66]/class pick <name>[-] to choose.");
            }
        }
        catch (Exception e)
        {
            Log.Warning("[DsClasses] Whisper to {0} failed: {1}", ci.playerName, e.Message);
        }
    }

    private void ApplyClassToPlayer(ClientInfo ci, ClassDef def)
    {
        var pid = ci.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid)) return;

        // Apply the signature buff. The player's entity might not be
        // resolvable yet on early spawns (race with world load), so
        // defer-and-retry pattern would be cleaner; for v0.1 we accept
        // best-effort and rely on the next OnPlayerSpawnedInWorld
        // (e.g. on respawn) to catch up.
        try
        {
            var entity = GameManager.Instance?.World?.GetEntity(ci.entityId) as EntityPlayer;
            if (entity?.Buffs != null)
            {
                entity.Buffs.AddBuff(def.BuffName);
            }
        }
        catch (Exception e)
        {
            Log.Warning("[DsClasses] Buff apply for {0} failed: {1}", ci.playerName, e.Message);
        }

        // Grant the class perm flag. Other DS plugins query this via
        // StyxCore.Perms.HasPermission(pid, "ds.class.<name>").
        try { StyxCore.Perms.GrantToPlayer(pid, def.PermFlag); }
        catch (Exception e) { Log.Warning("[DsClasses] Perm grant failed: " + e.Message); }
    }

    // ============================================================ assignment

    /// <summary>
    /// Set or change a player's class. Removes any previous class buff
    /// and revokes the previous perm flag, then applies the new ones.
    /// Saves the record to disk. Fires the OnDsClassChanged hook so
    /// other DS plugins can react (e.g. DS:Crafting refreshing recipe
    /// gates on class change).
    /// </summary>
    public bool AssignClass(string pid, string classKey, string assignedBy)
    {
        if (string.IsNullOrEmpty(pid) || string.IsNullOrEmpty(classKey)) return false;
        if (!Classes.TryGetValue(classKey, out var newDef))
        {
            Log.Warning("[DsClasses] AssignClass: unknown class '{0}'", classKey);
            return false;
        }

        var oldRecord = LoadRecord(pid);
        string oldKey = oldRecord?.ClassKey;

        // Revoke previous class's buff + perm if any.
        if (oldRecord != null && Classes.TryGetValue(oldRecord.ClassKey, out var oldDef))
        {
            RemoveClassFromPlayer(pid, oldDef);
        }

        // Save new record.
        var record = new PlayerClassRecord
        {
            ClassKey = newDef.Key,
            AssignedAtUtc = DateTime.UtcNow.ToString("o"),
            AssignedBy = assignedBy ?? "system",
        };
        if (!SaveRecord(pid, record)) return false;

        // Apply new buff + perm if the player is online.
        ClientInfo ci = null;
        var parsed = ParsePlatformIdSafe(pid);
        if (parsed != null)
        {
            try { ci = ConnectionManager.Instance?.Clients?.ForUserId(parsed); }
            catch (Exception e) { Log.Warning("[DsClasses] ForUserId lookup failed: " + e.Message); }
        }

        if (ci != null)
        {
            ApplyClassToPlayer(ci, newDef);
        }
        else
        {
            // Player is offline (or pid couldn't be parsed) -- grant the
            // perm now, the buff will be applied next time they spawn.
            try { StyxCore.Perms.GrantToPlayer(pid, newDef.PermFlag); }
            catch { }
        }

        // Fire the framework hook for other DS plugins to react.
        try { StyxCore.Hooks?.Fire("OnDsClassChanged", pid, oldKey, newDef.Key); }
        catch { }

        Log.Out("[DsClasses] {0} -> class={1} (assigned by {2})", pid, newDef.Key, record.AssignedBy);
        return true;
    }

    /// <summary>
    /// Clear a player's class assignment. Removes buff + perm + record
    /// file. Fires OnDsClassChanged with newKey=null. Idempotent.
    /// </summary>
    public bool ClearClass(string pid, string clearedBy)
    {
        var record = LoadRecord(pid);
        if (record == null) return false;
        string oldKey = record.ClassKey;

        if (Classes.TryGetValue(oldKey, out var oldDef))
        {
            RemoveClassFromPlayer(pid, oldDef);
        }

        try
        {
            var path = Path.Combine(_saveDir, pid + ".json");
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) { Log.Warning("[DsClasses] Failed to delete record: " + e.Message); }

        try { StyxCore.Hooks?.Fire("OnDsClassChanged", pid, oldKey, null); }
        catch { }

        Log.Out("[DsClasses] {0} class cleared (was {1}, by {2})", pid, oldKey, clearedBy ?? "system");
        return true;
    }

    private void RemoveClassFromPlayer(string pid, ClassDef def)
    {
        // Revoke perm.
        try { StyxCore.Perms.RevokeFromPlayer(pid, def.PermFlag); }
        catch (Exception e) { Log.Warning("[DsClasses] Perm revoke failed: " + e.Message); }

        // Remove buff if player is online.
        try
        {
            var parsed = ParsePlatformIdSafe(pid);
            if (parsed == null) return;
            var ci = ConnectionManager.Instance?.Clients?.ForUserId(parsed);
            if (ci != null && ci.entityId > 0)
            {
                var entity = GameManager.Instance?.World?.GetEntity(ci.entityId) as EntityPlayer;
                entity?.Buffs?.RemoveBuff(def.BuffName);
            }
        }
        catch (Exception e) { Log.Warning("[DsClasses] Buff remove failed: " + e.Message); }
    }

    // ============================================================ persistence

    private PlayerClassRecord LoadRecord(string pid)
    {
        try
        {
            var path = Path.Combine(_saveDir, pid + ".json");
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<PlayerClassRecord>(json);
        }
        catch (Exception e)
        {
            Log.Warning("[DsClasses] LoadRecord failed for {0}: {1}", pid, e.Message);
            return null;
        }
    }

    private bool SaveRecord(string pid, PlayerClassRecord record)
    {
        try
        {
            var path = Path.Combine(_saveDir, pid + ".json");
            File.WriteAllText(path, JsonConvert.SerializeObject(record, Formatting.Indented));
            return true;
        }
        catch (Exception e)
        {
            Log.Error("[DsClasses] SaveRecord failed for {0}: {1}", pid, e.Message);
            return false;
        }
    }

    private List<(string pid, PlayerClassRecord record)> LoadAllRecords()
    {
        var result = new List<(string, PlayerClassRecord)>();
        if (!Directory.Exists(_saveDir)) return result;
        foreach (var f in Directory.GetFiles(_saveDir, "*.json"))
        {
            try
            {
                var pid = Path.GetFileNameWithoutExtension(f);
                var json = File.ReadAllText(f);
                var record = JsonConvert.DeserializeObject<PlayerClassRecord>(json);
                if (record != null) result.Add((pid, record));
            }
            catch (Exception e)
            {
                Log.Warning("[DsClasses] LoadAllRecords skipped {0}: {1}", f, e.Message);
            }
        }
        return result;
    }

    // ============================================================ /class (player command)

    private void HandleClassCommand(CommandContext ctx, string[] args)
    {
        var pid = ctx.Client?.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid))
        {
            ctx.Reply("This command requires a player context.");
            return;
        }

        if (args == null || args.Length == 0)
        {
            ShowCurrentClass(ctx, pid);
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "list":
                ShowClassList(ctx);
                break;
            case "info":
                if (args.Length < 2) { ctx.Reply("Usage: /class info <name>"); break; }
                ShowClassInfo(ctx, args[1]);
                break;
            case "pick":
                if (args.Length < 2) { ctx.Reply("Usage: /class pick <name>"); break; }
                HandlePick(ctx, pid, args[1]);
                break;
            case "help":
            default:
                ctx.Reply("Commands:");
                ctx.Reply("  /class             -- show your current class");
                ctx.Reply("  /class list        -- list all classes");
                ctx.Reply("  /class info <name> -- show effects for a class");
                ctx.Reply("  /class pick <name> -- choose your class (one-time, ask admin to change)");
                break;
        }
    }

    private void ShowCurrentClass(CommandContext ctx, string pid)
    {
        var record = LoadRecord(pid);
        if (record == null || !Classes.TryGetValue(record.ClassKey, out var def))
        {
            ctx.Reply("[ffaa44]No class assigned.[-] Type [00ff66]/class list[-] then [00ff66]/class pick <name>[-].");
            return;
        }
        ctx.Reply("Your class: [00ff66]" + def.DisplayName + "[-]");
        ctx.Reply("  " + def.Flavor);
    }

    private void ShowClassList(CommandContext ctx)
    {
        ctx.Reply("[00ff66]Available classes (8):[-]");
        foreach (var def in Classes.Values)
        {
            ctx.Reply("  [88ddff]" + def.DisplayName + "[-]  -  " + def.Flavor);
        }
        ctx.Reply("Pick with [00ff66]/class pick <name>[-]. Detail: [00ff66]/class info <name>[-].");
    }

    private void ShowClassInfo(CommandContext ctx, string name)
    {
        if (!Classes.TryGetValue(name, out var def))
        {
            ctx.Reply("[ff6666]No class named '" + name + "'.[-] /class list to see all.");
            return;
        }
        ctx.Reply("[00ff66]" + def.DisplayName + "[-]");
        ctx.Reply("  " + def.Description);
        ctx.Reply("  Buff: " + def.BuffName + "  |  Perm: " + def.PermFlag);
    }

    private void HandlePick(CommandContext ctx, string pid, string name)
    {
        if (!Classes.TryGetValue(name, out var def))
        {
            ctx.Reply("[ff6666]No class named '" + name + "'.[-] /class list to see all.");
            return;
        }

        var existing = LoadRecord(pid);
        if (existing != null)
        {
            ctx.Reply("[ffaa44]You already have a class:[-] " + existing.ClassKey +
                ". Ask an admin to change it via /dsclass set.");
            return;
        }

        if (AssignClass(pid, def.Key, "self"))
        {
            ctx.Reply("[00ff66]Class set to " + def.DisplayName + ".[-] " + def.Flavor);
        }
        else
        {
            ctx.Reply("[ff6666]Failed to assign class.[-] See server log.");
        }
    }

    // ============================================================ /dsclass (admin command)

    private void HandleAdminCommand(CommandContext ctx, string[] args)
    {
        var pid = ctx.Client?.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid)) { ctx.Reply("Player context required."); return; }
        if (!StyxCore.Perms.HasPermission(pid, AdminPerm))
        {
            ctx.Reply("[ff6666]Permission denied:[-] requires '" + AdminPerm + "'.");
            return;
        }

        if (args == null || args.Length == 0)
        {
            ctx.Reply("Admin commands:");
            ctx.Reply("  /dsclass set <playerId> <classKey>  -- force-assign");
            ctx.Reply("  /dsclass clear <playerId>           -- clear assignment");
            ctx.Reply("  /dsclass info <playerId>            -- show their assignment");
            ctx.Reply("  /dsclass list                       -- list all assignments");
            ctx.Reply("  /dsclass stats                      -- counts per class");
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "set":
                if (args.Length < 3) { ctx.Reply("Usage: /dsclass set <playerId> <classKey>"); break; }
                HandleAdminSet(ctx, args[1], args[2]);
                break;
            case "clear":
                if (args.Length < 2) { ctx.Reply("Usage: /dsclass clear <playerId>"); break; }
                HandleAdminClear(ctx, args[1]);
                break;
            case "info":
                if (args.Length < 2) { ctx.Reply("Usage: /dsclass info <playerId>"); break; }
                HandleAdminInfo(ctx, args[1]);
                break;
            case "list":
                HandleAdminList(ctx);
                break;
            case "stats":
                HandleAdminStats(ctx);
                break;
            default:
                ctx.Reply("Unknown subcommand. /dsclass for help.");
                break;
        }
    }

    private void HandleAdminSet(CommandContext ctx, string targetPid, string classKey)
    {
        if (!Classes.ContainsKey(classKey))
        {
            ctx.Reply("[ff6666]Unknown class '" + classKey + "'.[-] /class list for all.");
            return;
        }
        if (AssignClass(targetPid, classKey, ctx.SenderName ?? "admin"))
        {
            ctx.Reply("[00ff66]Set " + targetPid + " -> " + classKey + ".[-]");
        }
        else
        {
            ctx.Reply("[ff6666]Assignment failed.[-] See server log.");
        }
    }

    private void HandleAdminClear(CommandContext ctx, string targetPid)
    {
        if (ClearClass(targetPid, ctx.SenderName ?? "admin"))
        {
            ctx.Reply("[ff6666]Cleared class for " + targetPid + ".[-]");
        }
        else
        {
            ctx.Reply("No class was assigned for " + targetPid + ".");
        }
    }

    private void HandleAdminInfo(CommandContext ctx, string targetPid)
    {
        var record = LoadRecord(targetPid);
        if (record == null)
        {
            ctx.Reply(targetPid + ": no class assigned.");
            return;
        }
        ctx.Reply(targetPid + ":");
        ctx.Reply("  Class:       " + record.ClassKey);
        ctx.Reply("  Assigned at: " + record.AssignedAtUtc);
        ctx.Reply("  Assigned by: " + record.AssignedBy);
    }

    private void HandleAdminList(CommandContext ctx)
    {
        var all = LoadAllRecords();
        if (all.Count == 0) { ctx.Reply("No class assignments on record."); return; }
        ctx.Reply("[00ff66]" + all.Count + " assignment(s):[-]");
        foreach (var (rpid, record) in all.OrderBy(r => r.record.ClassKey))
        {
            ctx.Reply("  " + rpid + "  ->  " + record.ClassKey);
        }
    }

    private void HandleAdminStats(CommandContext ctx)
    {
        var all = LoadAllRecords();
        ctx.Reply("[00ff66]DS:Classes assignment stats[-]");
        ctx.Reply("  Total assignments: " + all.Count);
        var counts = all.GroupBy(r => r.record.ClassKey)
                        .ToDictionary(g => g.Key, g => g.Count());
        foreach (var def in Classes.Values)
        {
            int n = counts.TryGetValue(def.Key, out var c) ? c : 0;
            ctx.Reply("  " + def.DisplayName.PadRight(14) + " " + n);
        }
    }

    // ============================================================ helpers

    /// <summary>
    /// Best-effort parse of a CombinedString platform id back to a
    /// PlatformUserIdentifierAbs for ConnectionManager lookups.
    /// Combined-string format is "PSN_xxx" / "Steam_xxx" / "EOS_xxx".
    /// Returns null on parse failure rather than throwing.
    /// </summary>
    private static PlatformUserIdentifierAbs ParsePlatformIdSafe(string combined)
    {
        if (string.IsNullOrEmpty(combined)) return null;
        try
        {
            // TryFromCombinedString is the preferred non-throwing API
            // for parsing the "PSN_xxx" / "Steam_xxx" / "EOS_xxx" form.
            if (PlatformUserIdentifierAbs.TryFromCombinedString(combined, out var id))
                return id;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Public read API for other DS plugins. Returns the assigned class
    /// key for the player, or null if unassigned. Plugins should
    /// generally prefer perm-flag checks (StyxCore.Perms.HasPermission)
    /// since those don't require knowing about DsClasses internals,
    /// but this is here when the class key itself matters (e.g. for
    /// branching logic on multiple class flags).
    /// </summary>
    public static string GetClassKey(string pid)
    {
        if (string.IsNullOrEmpty(pid)) return null;
        // Re-resolve _saveDir lazily since this is a static API and the
        // instance reference isn't cleanly available; use the data path
        // directly. Inefficient for a hot path but rare in v0.1.
        try
        {
            var dir = Path.Combine(StyxCore.DataPath(), "DsClasses");
            var path = Path.Combine(dir, pid + ".json");
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var record = JsonConvert.DeserializeObject<PlayerClassRecord>(json);
            return record?.ClassKey;
        }
        catch { return null; }
    }
}
