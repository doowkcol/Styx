// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// PermEditor — UI-driven permission management for groups.
//
// Three-stage navigation (one window, stage cvar gates which rect shows):
//   stage 0 → pick a GROUP    (8 rows max)
//   stage 1 → pick a PLUGIN   (12 rows max, "(All)" is row 0 — show every perm)
//   stage 2 → edit the group × plugin slice:
//              3 leading config rows (priority / tag / color) + perms filtered
//              to the chosen plugin (16 rows max, plenty when filtered)
//
// Why three stages instead of one: we've passed ~20 registered perms and the
// single-page list was silently truncating (.Take(16)). The plugin pick is
// stable + small (one row per plugin) so it's the natural cut.
//
// Player → group assignment STAYS in chat (`/perm addto <player> <group>`).
// That's the only "dynamic string" piece and chat is fine for it.
//
// Permission required: styx.perm.admin (same as PermManager mutations).
//
// Limitations:
//   - Labels are baked at OnServerInitialized → groups / perms / plugin
//     owners added LATER won't appear in UI until next server restart.
//     Acceptable since this is rare admin work.
//   - 8 group rows + 12 plugin rows + 16 perm rows max in v0.2; bump
//     constants + mirror rows in XUi if a server grows beyond that.

using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Permissions;
using Styx.Plugins;

[Info("PermEditor", "Doowkcol", "0.2.0")]
public class PermEditor : StyxPlugin
{
    public override string Description => "UI for granting/revoking plugin perms per group (group → plugin → perms)";

    private const string PermAdmin = "styx.perm.admin";  // Same gate as PermManager

    private const int MaxGroupRows  = 8;
    private const int MaxPluginRows = 20;
    private const int MaxPermRows   = 16;
    private const int ConfigRowCount = 3;  // Stage 2 has 3 leading config rows: priority / tag / color

    // Stage numbers — shared with windows.xml `{#cvar('styx.pe.stage')}` visibility.
    private const int STAGE_GROUPS  = 0;
    private const int STAGE_PLUGINS = 1;
    private const int STAGE_PERMS   = 2;

    // "(All)" sentinel — first entry in every plugin-picker list. When the
    // player picks this row, stage 2 shows EVERY known perm (unfiltered).
    private const string AllPluginsLabel = "(All plugins)";

    // Preset cycles for chat-tag editing. Index 0 of TagPresets is the "no
    // tag" sentinel — selecting it sets ChatTag = null. Storing presets
    // statically lets us register them as static labels for indexed lookup
    // (Recipe B), avoiding the dynamic-string painting limitation of XUi.
    private static readonly string[] TagPresets =
    {
        "[None]",     // index 0 → null tag
        "[Player]",
        "[Builder]",
        "[Helper]",
        "[VIP]",
        "[Mod]",
        "[Admin]",
        "[Owner]",
    };

    // ChatTagColor preset cycle — array of (BBCode hex, friendly name).
    private static readonly string[] ColorPresets =
    {
        "ffffff",  // white
        "888888",  // gray
        "00ff66",  // green
        "55aaff",  // blue
        "ffaa00",  // gold
        "ff6666",  // red
        "ff66ff",  // magenta
        "00ffff",  // cyan
    };
    private static readonly string[] ColorNames =
    {
        "white", "gray", "green", "blue", "gold", "red", "magenta", "cyan",
    };

    // Priority cycle: 0..200 in steps of 10.
    private const int PriorityStep = 10;
    private const int PriorityMax  = 200;

    // Per-player session state
    private readonly HashSet<int> _open = new HashSet<int>();
    private readonly Dictionary<int, string> _selectedGroup  = new Dictionary<int, string>();
    /// <summary>The plugin-owner string the user picked, or null = show all.</summary>
    private readonly Dictionary<int, string> _selectedPlugin = new Dictionary<int, string>();

    // Snapshots taken at OpenFor — index-stable for the duration of one
    // menu session. New groups/perms/plugins added after open won't show
    // until the menu is reopened.
    private readonly Dictionary<int, List<GroupData>> _groupSnapshot =
        new Dictionary<int, List<GroupData>>();
    /// <summary>ALL known perms — stage 2 filters this by selected plugin
    /// before rendering. Not capped at MaxPermRows so the full registry
    /// survives; per-plugin slices comfortably fit the XUi row budget.</summary>
    private readonly Dictionary<int, List<PermissionManager.KnownPermission>> _permSnapshot =
        new Dictionary<int, List<PermissionManager.KnownPermission>>();
    /// <summary>Ordered plugin-owner list for the stage 1 picker. Index 0
    /// is always the "(All plugins)" sentinel — picking it means the stage 2
    /// perm slice is unfiltered.</summary>
    private readonly Dictionary<int, List<string>> _pluginSnapshot =
        new Dictionary<int, List<string>>();

    public override void OnLoad()
    {
        Styx.Ui.Menu.Register(this, "Perm Editor", OpenFor, permission: PermAdmin);

        Styx.Ui.Ephemeral.Register(
            "styx.pe.open", "styx.pe.stage", "styx.pe.sel",
            "styx.pe.group_count", "styx.pe.plugin_count", "styx.pe.perm_count",
            "styx.pe.selected_group_id", "styx.pe.selected_plugin_id",
            // Group config row cvars (stage 2 leading rows)
            "styx.pe.cfg_priority", "styx.pe.cfg_tag_id", "styx.pe.cfg_color_id");
        for (int i = 0; i < MaxGroupRows; i++)
            Styx.Ui.Ephemeral.Register("styx.pe.group" + i + "_id");
        for (int i = 0; i < MaxPluginRows; i++)
            Styx.Ui.Ephemeral.Register("styx.pe.plugin" + i + "_id");
        for (int i = 0; i < MaxPermRows; i++)
        {
            Styx.Ui.Ephemeral.Register(
                "styx.pe.perm" + i + "_id",
                "styx.pe.perm" + i + "_status");
        }

        StyxCore.Perms.RegisterKnown(PermAdmin,
            "Open the /m → Perm Editor sub-menu", Name);

        Log.Out("[PermEditor] Loaded v0.2.0 — perm: {0} (labels built at OnServerInitialized)", PermAdmin);
    }

    /// <summary>
    /// Defer label baking until ALL plugins have loaded — otherwise plugins
    /// that register perms after PermEditor (whichever way the load order
    /// resolves them) won't have static labels and would render as the raw
    /// placeholder key (e.g. "perm_def_3").
    /// </summary>
    void OnServerInitialized()
    {
        BuildLabels();
        var perms = StyxCore.Perms.AllKnown;
        Log.Out("[PermEditor] Labels built — {0} groups, {1} known perms, {2} plugin owners ready",
            StyxCore.Perms.GetAllGroups().Count, perms.Count, DistinctOwners(perms).Count);
    }

    public override void OnUnload()
    {
        foreach (var eid in _open)
        {
            var p = StyxCore.Player.FindByEntityId(eid);
            if (p != null)
            {
                Styx.Ui.SetVar(p, "styx.pe.open", 0f);
                Styx.Ui.Input.Release(p, Name);
            }
        }
        _open.Clear();
        _selectedGroup.Clear();
        _selectedPlugin.Clear();
        _groupSnapshot.Clear();
        _permSnapshot.Clear();
        _pluginSnapshot.Clear();
        Styx.Ui.Menu.UnregisterAll(this);
        Styx.Ui.Labels.UnregisterAll(this);
        StyxCore.Perms.UnregisterKnownByOwner(Name);
    }

    // ================================================================== labels

    /// <summary>Distinct, alpha-sorted list of plugin owners across AllKnown,
    /// prefixed by the "(All plugins)" sentinel at index 0.</summary>
    private static List<string> DistinctOwners(IReadOnlyList<PermissionManager.KnownPermission> perms)
    {
        var list = new List<string> { AllPluginsLabel };
        list.AddRange(perms
            .Select(k => k.Owner)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        return list;
    }

    private void BuildLabels()
    {
        var groups = StyxCore.Perms.GetAllGroups();
        for (int i = 0; i < groups.Count && i < 32; i++)
            Styx.Ui.Labels.Register(this, "perm_grp_" + i, groups[i].Name);
        // Pad unused slots so XUi resolves the binding to SOMETHING
        for (int i = groups.Count; i < 32; i++)
            Styx.Ui.Labels.Register(this, "perm_grp_" + i, "");

        var perms = StyxCore.Perms.AllKnown;
        // Up to 128 perms registered as labels (perm_def_N / perm_desc_N).
        // Stage 2 uses a filtered slice ≤ MaxPermRows at a time, but the full
        // registry must be addressable because indices are stable across
        // filtered vs unfiltered views.
        const int MaxLabelSlots = 128;
        for (int i = 0; i < perms.Count && i < MaxLabelSlots; i++)
        {
            Styx.Ui.Labels.Register(this, "perm_def_" + i, perms[i].Name);
            Styx.Ui.Labels.Register(this, "perm_desc_" + i,
                string.IsNullOrEmpty(perms[i].Description) ? "(no description)" : perms[i].Description);
        }
        for (int i = perms.Count; i < MaxLabelSlots; i++)
        {
            Styx.Ui.Labels.Register(this, "perm_def_" + i, "");
            Styx.Ui.Labels.Register(this, "perm_desc_" + i, "");
        }

        // Plugin-owner labels (perm_plugin_N).
        var owners = DistinctOwners(perms);
        for (int i = 0; i < owners.Count && i < 32; i++)
            Styx.Ui.Labels.Register(this, "perm_plugin_" + i, owners[i]);
        for (int i = owners.Count; i < 32; i++)
            Styx.Ui.Labels.Register(this, "perm_plugin_" + i, "");

        // Tag preset labels (perm_tag_0..N) — rendered via XUi
        // {#localization('perm_tag_' + int(cvar('styx.pe.cfg_tag_id')))}
        for (int i = 0; i < TagPresets.Length; i++)
            Styx.Ui.Labels.Register(this, "perm_tag_" + i, TagPresets[i]);

        // Color preset labels — display the friendly name, not the hex
        for (int i = 0; i < ColorNames.Length; i++)
            Styx.Ui.Labels.Register(this, "perm_color_" + i, ColorNames[i]);
    }

    private static int IndexOfTagPreset(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return 0;  // [None]
        for (int i = 0; i < TagPresets.Length; i++)
            if (string.Equals(TagPresets[i], tag, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;  // unknown → treat as none
    }

    private static int IndexOfColorPreset(string color)
    {
        if (string.IsNullOrEmpty(color)) return 0;  // white default
        for (int i = 0; i < ColorPresets.Length; i++)
            if (string.Equals(ColorPresets[i], color, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    // ================================================================== open / close

    private void OpenFor(EntityPlayer p)
    {
        if (p == null) return;
        string pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid)) return;

        if (!StyxCore.Perms.HasPermission(pid, PermAdmin))
        {
            Styx.Server.Whisper(p, "[ff6666][PermEditor] You lack permission '" + PermAdmin + "'.[-]");
            return;
        }

        // Snapshot stable indices at open time. Groups and plugin owners are
        // display-row sized; the perm list is kept full (filtering happens
        // at stage 2 paint time).
        var groups  = StyxCore.Perms.GetAllGroups().Take(MaxGroupRows).ToList();
        var perms   = StyxCore.Perms.AllKnown.ToList();
        var plugins = DistinctOwners(perms).Take(MaxPluginRows).ToList();

        if (groups.Count == 0)
        {
            Styx.Server.Whisper(p, "[ffaa00][PermEditor] No groups defined.[-]");
            return;
        }

        _open.Add(p.entityId);
        _groupSnapshot[p.entityId]  = groups;
        _permSnapshot[p.entityId]   = perms;
        _pluginSnapshot[p.entityId] = plugins;
        _selectedGroup.Remove(p.entityId);
        _selectedPlugin.Remove(p.entityId);

        // Render stage 0 (group picker)
        Styx.Ui.SetVar(p, "styx.pe.open", 1f);
        Styx.Ui.SetVar(p, "styx.pe.stage", STAGE_GROUPS);
        Styx.Ui.SetVar(p, "styx.pe.sel", 0f);
        Styx.Ui.SetVar(p, "styx.pe.group_count", groups.Count);

        for (int i = 0; i < MaxGroupRows; i++)
        {
            // Index into perm_grp_N labels — find the group's row in BuildLabels' ordering
            int labelIdx = i < groups.Count
                ? IndexOfGroupInRegistry(groups[i].Name)
                : 0;
            Styx.Ui.SetVar(p, "styx.pe.group" + i + "_id", labelIdx);
        }

        Styx.Ui.Input.Acquire(p, Name);
        WhisperGroupRow(p, 0);
    }

    private void CloseFor(EntityPlayer p)
    {
        if (p == null) return;
        Styx.Ui.SetVar(p, "styx.pe.open", 0f);
        Styx.Ui.Input.Release(p, Name);
        _open.Remove(p.entityId);
        _selectedGroup.Remove(p.entityId);
        _selectedPlugin.Remove(p.entityId);
        _groupSnapshot.Remove(p.entityId);
        _permSnapshot.Remove(p.entityId);
        _pluginSnapshot.Remove(p.entityId);
    }

    // ================================================================== input dispatch

    void OnPlayerInput(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (p?.Buffs == null || !_open.Contains(p.entityId)) return;
        if ((int)p.Buffs.GetCustomVar("styx.pe.open") != 1) return;

        int stage = (int)p.Buffs.GetCustomVar("styx.pe.stage");
        switch (stage)
        {
            case STAGE_GROUPS:  HandleStageGroups(p, kind);  break;
            case STAGE_PLUGINS: HandleStagePlugins(p, kind); break;
            case STAGE_PERMS:   HandleStagePerms(p, kind);   break;
        }
    }

    // ================================================================== stage 0 — groups

    private void HandleStageGroups(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (!_groupSnapshot.TryGetValue(p.entityId, out var groups)) return;
        int sel = (int)p.Buffs.GetCustomVar("styx.pe.sel");
        int count = groups.Count;

        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
                sel = (sel + 1) % count;
                Styx.Ui.SetVar(p, "styx.pe.sel", sel);
                WhisperGroupRow(p, sel);
                break;

            case Styx.Ui.StyxInputKind.Crouch:
                sel = (sel - 1 + count) % count;
                Styx.Ui.SetVar(p, "styx.pe.sel", sel);
                WhisperGroupRow(p, sel);
                break;

            case Styx.Ui.StyxInputKind.PrimaryAction:
                EnterStagePlugins(p, groups[sel]);
                break;

            case Styx.Ui.StyxInputKind.SecondaryAction:
                CloseFor(p);
                Styx.Scheduling.Scheduler.Once(0.05,
                    () => Styx.Ui.Menu.OpenLauncher?.Invoke(p),
                    name: "PermEditor.BackToLauncher");
                break;
        }
    }

    // ================================================================== stage 1 — plugins

    private void EnterStagePlugins(EntityPlayer p, GroupData group)
    {
        _selectedGroup[p.entityId] = group.Name;
        if (!_pluginSnapshot.TryGetValue(p.entityId, out var plugins)) return;

        Styx.Ui.SetVar(p, "styx.pe.stage", STAGE_PLUGINS);
        Styx.Ui.SetVar(p, "styx.pe.sel", 0f);
        Styx.Ui.SetVar(p, "styx.pe.plugin_count", plugins.Count);
        Styx.Ui.SetVar(p, "styx.pe.selected_group_id", IndexOfGroupInRegistry(group.Name));

        for (int i = 0; i < MaxPluginRows; i++)
        {
            int labelIdx = i < plugins.Count
                ? IndexOfPluginInRegistry(plugins[i])
                : 0;
            Styx.Ui.SetVar(p, "styx.pe.plugin" + i + "_id", labelIdx);
        }

        WhisperPluginRow(p, 0);
    }

    private void HandleStagePlugins(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (!_pluginSnapshot.TryGetValue(p.entityId, out var plugins)) return;
        int sel = (int)p.Buffs.GetCustomVar("styx.pe.sel");
        int count = plugins.Count;
        if (count == 0) { CloseFor(p); return; }

        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
                sel = (sel + 1) % count;
                Styx.Ui.SetVar(p, "styx.pe.sel", sel);
                WhisperPluginRow(p, sel);
                break;

            case Styx.Ui.StyxInputKind.Crouch:
                sel = (sel - 1 + count) % count;
                Styx.Ui.SetVar(p, "styx.pe.sel", sel);
                WhisperPluginRow(p, sel);
                break;

            case Styx.Ui.StyxInputKind.PrimaryAction:
                string picked = plugins[sel];
                // Row 0 is the "(All plugins)" sentinel — store null to mean unfiltered.
                _selectedPlugin[p.entityId] =
                    string.Equals(picked, AllPluginsLabel, StringComparison.OrdinalIgnoreCase) ? null : picked;
                EnterStagePerms(p);
                break;

            case Styx.Ui.StyxInputKind.SecondaryAction:
                // Back to group picker
                _selectedGroup.Remove(p.entityId);
                Styx.Ui.SetVar(p, "styx.pe.stage", STAGE_GROUPS);
                Styx.Ui.SetVar(p, "styx.pe.sel", 0f);
                WhisperGroupRow(p, 0);
                break;
        }
    }

    // ================================================================== stage 2 — perms

    /// <summary>Build the filtered perm list for the current selection.
    /// Null filter = all; otherwise case-insensitive owner match.</summary>
    private List<PermissionManager.KnownPermission> FilteredPerms(int entityId)
    {
        if (!_permSnapshot.TryGetValue(entityId, out var all)) return new List<PermissionManager.KnownPermission>();
        _selectedPlugin.TryGetValue(entityId, out var filter);
        IEnumerable<PermissionManager.KnownPermission> q = all;
        if (!string.IsNullOrEmpty(filter))
            q = q.Where(k => string.Equals(k.Owner, filter, StringComparison.OrdinalIgnoreCase));
        return q.Take(MaxPermRows).ToList();
    }

    private void EnterStagePerms(EntityPlayer p)
    {
        if (!_selectedGroup.TryGetValue(p.entityId, out var groupName)) return;
        var group = StyxCore.Perms.GetGroup(groupName);
        if (group == null) return;

        var perms = FilteredPerms(p.entityId);

        Styx.Ui.SetVar(p, "styx.pe.stage", STAGE_PERMS);
        Styx.Ui.SetVar(p, "styx.pe.sel", 0f);
        Styx.Ui.SetVar(p, "styx.pe.perm_count", perms.Count);
        // Tell XUi which plugin row to highlight in the "EDITING ... / plugin" subtitle.
        _selectedPlugin.TryGetValue(p.entityId, out var filter);
        Styx.Ui.SetVar(p, "styx.pe.selected_plugin_id",
            IndexOfPluginInRegistry(string.IsNullOrEmpty(filter) ? AllPluginsLabel : filter));

        // Populate the 3 leading config rows (priority, tag, color).
        Styx.Ui.SetVar(p, "styx.pe.cfg_priority", group.Priority);
        Styx.Ui.SetVar(p, "styx.pe.cfg_tag_id",   IndexOfTagPreset(group.ChatTag));
        Styx.Ui.SetVar(p, "styx.pe.cfg_color_id", IndexOfColorPreset(group.ChatTagColor));

        RefreshPermStatuses(p, group, perms);
        WhisperRow(p, 0);
    }

    // Row layout on stage 2:
    //   sel 0 → Priority      (LMB cycles +10, wrap at PriorityMax)
    //   sel 1 → Chat Tag      (LMB cycles to next preset)
    //   sel 2 → Chat Color    (LMB cycles to next preset)
    //   sel 3..3+N-1 → Perms  (LMB toggles grant)
    //
    // All edits respect the auth guard (CanActorEditGroup).

    private void HandleStagePerms(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        var perms = FilteredPerms(p.entityId);

        int sel = (int)p.Buffs.GetCustomVar("styx.pe.sel");
        int totalRows = ConfigRowCount + perms.Count;
        if (totalRows == 0) { CloseFor(p); return; }

        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
                sel = (sel + 1) % totalRows;
                Styx.Ui.SetVar(p, "styx.pe.sel", sel);
                WhisperRow(p, sel);
                break;

            case Styx.Ui.StyxInputKind.Crouch:
                sel = (sel - 1 + totalRows) % totalRows;
                Styx.Ui.SetVar(p, "styx.pe.sel", sel);
                WhisperRow(p, sel);
                break;

            case Styx.Ui.StyxInputKind.PrimaryAction:
                if (sel == 0)      CyclePriority(p);
                else if (sel == 1) CycleTag(p);
                else if (sel == 2) CycleColor(p);
                else               TogglePerm(p, perms[sel - ConfigRowCount].Name);
                break;

            case Styx.Ui.StyxInputKind.SecondaryAction:
                // Back to plugin picker
                Styx.Ui.SetVar(p, "styx.pe.stage", STAGE_PLUGINS);
                Styx.Ui.SetVar(p, "styx.pe.sel", 0f);
                WhisperPluginRow(p, 0);
                break;
        }
    }

    // ================================================================== group config cycle handlers

    /// <summary>Auth + selected-group lookup. Returns (groupName, group) on
    /// success or (null, null) and whispers an error on failure.</summary>
    private (string name, GroupData data) ResolveEditableGroup(EntityPlayer p)
    {
        if (!_selectedGroup.TryGetValue(p.entityId, out var groupName)) return (null, null);
        var group = StyxCore.Perms.GetGroup(groupName);
        if (group == null) return (null, null);

        var actorPid = StyxCore.Player.PlatformIdOf(p);
        if (!StyxCore.Perms.CanActorEditGroup(actorPid, groupName))
        {
            int actorAuth = StyxCore.Perms.GetAuthLevel(actorPid);
            Styx.Server.Whisper(p, string.Format(
                "[ff6666][PermEditor] Refused — group '{0}' contains a member with more authority than you (your auth {1}).[-]",
                groupName, actorAuth));
            return (null, null);
        }
        return (groupName, group);
    }

    private void CyclePriority(EntityPlayer p)
    {
        var (name, group) = ResolveEditableGroup(p);
        if (group == null) return;

        int newPriority = group.Priority + PriorityStep;
        if (newPriority > PriorityMax) newPriority = 0;
        StyxCore.Perms.SetGroupPriority(name, newPriority);
        Styx.Ui.SetVar(p, "styx.pe.cfg_priority", newPriority);
        Styx.Server.Whisper(p, string.Format(
            "[00ff66][PermEditor] '{0}' priority -> {1}[-]", name, newPriority));
    }

    private void CycleTag(EntityPlayer p)
    {
        var (name, group) = ResolveEditableGroup(p);
        if (group == null) return;

        int currentIdx = IndexOfTagPreset(group.ChatTag);
        int nextIdx = (currentIdx + 1) % TagPresets.Length;
        // Index 0 = "[None]" sentinel → store null so GetGroupTag treats it as "no tag"
        string newTag = nextIdx == 0 ? null : TagPresets[nextIdx];
        StyxCore.Perms.SetGroupTag(name, newTag, group.ChatTagColor);
        Styx.Ui.SetVar(p, "styx.pe.cfg_tag_id", nextIdx);
        Styx.Server.Whisper(p, string.Format(
            "[00ff66][PermEditor] '{0}' tag -> {1}[-]", name, TagPresets[nextIdx]));
    }

    private void CycleColor(EntityPlayer p)
    {
        var (name, group) = ResolveEditableGroup(p);
        if (group == null) return;

        int currentIdx = IndexOfColorPreset(group.ChatTagColor);
        int nextIdx = (currentIdx + 1) % ColorPresets.Length;
        string newColor = ColorPresets[nextIdx];
        StyxCore.Perms.SetGroupTag(name, group.ChatTag, newColor);
        Styx.Ui.SetVar(p, "styx.pe.cfg_color_id", nextIdx);
        Styx.Server.Whisper(p, string.Format(
            "[00ff66][PermEditor] '{0}' color -> [{1}]{2}[-][-]",
            name, newColor, ColorNames[nextIdx]));
    }

    /// <summary>Dispatch whisper based on row type — config rows show the
    /// current value, perm rows show grant status + description.</summary>
    private void WhisperRow(EntityPlayer p, int row)
    {
        if (!_selectedGroup.TryGetValue(p.entityId, out var groupName)) return;
        var group = StyxCore.Perms.GetGroup(groupName);
        if (group == null) return;

        if (row == 0)
        {
            Styx.Server.Whisper(p, string.Format(
                "[ccddff][PermEditor] '{0}' priority:[-] [ffffdd]{1}[-] (LMB cycles +{2}, wraps at {3})",
                groupName, group.Priority, PriorityStep, PriorityMax));
        }
        else if (row == 1)
        {
            int idx = IndexOfTagPreset(group.ChatTag);
            Styx.Server.Whisper(p, string.Format(
                "[ccddff][PermEditor] '{0}' tag:[-] [ffffdd]{1}[-] (LMB cycles to next preset)",
                groupName, TagPresets[idx]));
        }
        else if (row == 2)
        {
            int idx = IndexOfColorPreset(group.ChatTagColor);
            Styx.Server.Whisper(p, string.Format(
                "[ccddff][PermEditor] '{0}' color:[-] [{1}]{2}[-] (LMB cycles to next preset)",
                groupName, ColorPresets[idx], ColorNames[idx]));
        }
        else
        {
            WhisperPermRow(p, row - ConfigRowCount);
        }
    }

    private void TogglePerm(EntityPlayer p, string permName)
    {
        if (!_selectedGroup.TryGetValue(p.entityId, out var groupName)) return;
        var group = StyxCore.Perms.GetGroup(groupName);
        if (group == null) return;

        // Privilege-escalation guard: can't edit a group that contains a
        // higher-authority member than yourself. Owner bypasses this.
        var actorPid = StyxCore.Player.PlatformIdOf(p);
        if (!StyxCore.Perms.CanActorEditGroup(actorPid, groupName))
        {
            int actorAuth = StyxCore.Perms.GetAuthLevel(actorPid);
            Styx.Server.Whisper(p, string.Format(
                "[ff6666][PermEditor] Refused — group '{0}' contains a member with more authority than you (your auth {1}).[-]",
                groupName, actorAuth));
            return;
        }

        bool currentlyGranted = group.Perms.Contains(permName);
        if (currentlyGranted)
        {
            StyxCore.Perms.RevokeFromGroup(groupName, permName);
            Styx.Server.Whisper(p, string.Format(
                "[ffaa00][PermEditor] Revoked [-][ffffdd]{0}[-] from group [ffffdd]{1}[-]",
                permName, groupName));
        }
        else
        {
            StyxCore.Perms.GrantToGroup(groupName, permName);
            Styx.Server.Whisper(p, string.Format(
                "[00ff66][PermEditor] Granted [-][ffffdd]{0}[-] to group [ffffdd]{1}[-]",
                permName, groupName));
        }

        // Refresh badges (the group reference may have stale Perms set since
        // GroupData is by-value here; re-fetch and re-paint the filtered slice).
        var refreshed = StyxCore.Perms.GetGroup(groupName);
        if (refreshed != null)
            RefreshPermStatuses(p, refreshed, FilteredPerms(p.entityId));
    }

    private void RefreshPermStatuses(EntityPlayer p, GroupData group,
        List<PermissionManager.KnownPermission> perms)
    {
        Styx.Ui.SetVar(p, "styx.pe.perm_count", perms.Count);
        for (int i = 0; i < MaxPermRows; i++)
        {
            int labelIdx = i < perms.Count
                ? IndexOfPermInRegistry(perms[i].Name)
                : 0;
            int status = i < perms.Count && group.Perms.Contains(perms[i].Name)
                ? 1 : 0;
            Styx.Ui.SetVar(p, "styx.pe.perm" + i + "_id", labelIdx);
            Styx.Ui.SetVar(p, "styx.pe.perm" + i + "_status", status);
        }
    }

    // ================================================================== whisper helpers

    private void WhisperGroupRow(EntityPlayer p, int row)
    {
        if (!_groupSnapshot.TryGetValue(p.entityId, out var groups)) return;
        if (row < 0 || row >= groups.Count) return;
        var g = groups[row];
        string tagInfo = string.IsNullOrEmpty(g.ChatTag)
            ? "[888888]no tag[-]"
            : "[" + (string.IsNullOrEmpty(g.ChatTagColor) ? "ffffff" : g.ChatTagColor) + "]" + g.ChatTag + "[-]";
        Styx.Server.Whisper(p, string.Format(
            "[ccddff][PermEditor] {0}/{1}:[-] [ffffdd]{2}[-] (priority {3}, {4}, {5} perm(s))",
            row + 1, groups.Count, g.Name, g.Priority, tagInfo, g.Perms.Count));
    }

    private void WhisperPluginRow(EntityPlayer p, int row)
    {
        if (!_pluginSnapshot.TryGetValue(p.entityId, out var plugins)) return;
        if (row < 0 || row >= plugins.Count) return;
        string name = plugins[row];
        // Count perms owned by this plugin for a helpful hint.
        int permCount;
        if (string.Equals(name, AllPluginsLabel, StringComparison.OrdinalIgnoreCase))
            permCount = _permSnapshot.TryGetValue(p.entityId, out var all) ? all.Count : 0;
        else
            permCount = _permSnapshot.TryGetValue(p.entityId, out var all2)
                ? all2.Count(k => string.Equals(k.Owner, name, StringComparison.OrdinalIgnoreCase))
                : 0;
        Styx.Server.Whisper(p, string.Format(
            "[ccddff][PermEditor] {0}/{1}:[-] [ffffdd]{2}[-] ({3} perm(s))",
            row + 1, plugins.Count, name, permCount));
    }

    private void WhisperPermRow(EntityPlayer p, int row)
    {
        var perms = FilteredPerms(p.entityId);
        if (row < 0 || row >= perms.Count) return;
        if (!_selectedGroup.TryGetValue(p.entityId, out var gName)) return;
        var group = StyxCore.Perms.GetGroup(gName);
        var perm = perms[row];
        bool granted = group?.Perms.Contains(perm.Name) == true;
        string status = granted ? "[00ff66]GRANTED[-]" : "[888888]not granted[-]";
        Styx.Server.Whisper(p, string.Format(
            "[ccddff][PermEditor] {0}/{1}:[-] [ffffdd]{2}[-] — {3}  ({4}) [{5}]",
            row + 1, perms.Count, perm.Name, status,
            string.IsNullOrEmpty(perm.Description) ? "(no description)" : perm.Description,
            perm.Owner));
    }

    // ================================================================== index lookups

    private int IndexOfGroupInRegistry(string name)
    {
        var all = StyxCore.Perms.GetAllGroups();
        for (int i = 0; i < all.Count && i < 32; i++)
            if (string.Equals(all[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    private int IndexOfPermInRegistry(string permName)
    {
        var all = StyxCore.Perms.AllKnown;
        for (int i = 0; i < all.Count && i < 128; i++)
            if (string.Equals(all[i].Name, permName, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    /// <summary>Lookup index into the perm_plugin_N label set. BuildLabels
    /// registers these in the same order DistinctOwners produces (sentinel
    /// "(All plugins)" at 0, then alpha-sorted owners).</summary>
    private int IndexOfPluginInRegistry(string pluginName)
    {
        if (string.IsNullOrEmpty(pluginName)) return 0;
        var owners = DistinctOwners(StyxCore.Perms.AllKnown);
        for (int i = 0; i < owners.Count && i < 32; i++)
            if (string.Equals(owners[i], pluginName, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }
}
