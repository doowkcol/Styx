// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// StyxScrollInput -- routes mouse-wheel slot changes to menu navigation.
//
// Mechanism (per STYX_INPUT_DETECTION.md §B + STYX_ENTITY_HOOKS.md §4):
// Scrolling the mouse wheel cycles the player's hotbar slot. The engine
// fires EntityAlive.OnHoldingItemChanged server-side on every slot change,
// regardless of source (scroll, hotkey 1-9, drop, equip).
//
// We Postfix that virtual, track per-player previous slot, compute the
// delta. Adjacent moves (delta +/-1, or wrap +/-9 across the 10-slot
// hotbar) are scroll-wheel events; non-adjacent moves are hotkey jumps
// (the player pressed "5" while on slot 0) and are ignored.
//
// When a Styx menu is open for the player (gated by buffStyxInputProbe
// presence -- the framework auto-applies this on Styx.Ui.Input.Acquire),
// we re-dispatch the scroll as the existing StyxInputKind.Jump (next)
// or StyxInputKind.Crouch (previous) via StyxCore.Hooks.Fire. Every
// existing menu plugin (StyxMenu, Kit, PermEditor, AdminTools, StyxPerms,
// /m launcher) picks it up unchanged.
//
// Cosmetic side effect: scrolling while a menu is open also changes the
// visible held item. Same precedent as jump-nav making the player jump --
// unavoidable without a client mod.
//
// Commands:
//   /scroll status         report state
//   /scroll diag on|off    toggle verbose logging (see every slot change)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Styx;
using Styx.Plugins;

[Info("StyxScrollInput", "Doowkcol", "0.1.2")]
public class StyxScrollInput : StyxPlugin
{
    public override string Description => "Mouse-wheel menu navigation via OnHoldingItemChanged";

    private const int ToolbeltSize = 10;          // slots 0..9
    private const int WrapDelta = ToolbeltSize - 1; // 9

    // Per-player last-known slot. Keyed by entityId. Main-thread-only
    // access (Harmony patches + hook callbacks both run on Unity main),
    // so a plain Dictionary is fine.
    private static readonly Dictionary<int, int> _lastSlot = new Dictionary<int, int>();

    internal static bool Diag;

    // Styx.Ui.Input.Dispatch is internal -- reflect once, cache the
    // MethodInfo. This is the canonical entry the framework's own
    // NetPackageGameEventRequest Prefix uses, so calling it routes
    // through the same Styx.Ui.Input.OnInput event the plugin hook
    // bus subscribes to (-> StyxMenu.OnPlayerInput etc.).
    private static MethodInfo _dispatch;
    private static MethodInfo DispatchMethod
    {
        get
        {
            if (_dispatch == null)
            {
                _dispatch = typeof(Styx.Ui.Input).GetMethod(
                    "Dispatch",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    new[] { typeof(EntityPlayer), typeof(Styx.Ui.StyxInputKind) },
                    null);
                if (_dispatch == null)
                    Log.Error("[StyxScrollInput] Could not find Styx.Ui.Input.Dispatch via reflection");
            }
            return _dispatch;
        }
    }

    static void Dispatch(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        var m = DispatchMethod;
        if (m == null) return;
        try { m.Invoke(null, new object[] { p, kind }); }
        catch (Exception e) { Log.Error("[StyxScrollInput] Dispatch threw: " + e); }
    }

    public override void OnLoad()
    {
        StyxCore.Commands.Register("scroll", "Scroll input router -- /scroll [status|diag on|off|inspect]", (ctx, args) =>
        {
            if (args.Length >= 2 && args[0].ToLowerInvariant() == "diag")
            {
                Diag = args[1].ToLowerInvariant() == "on";
                ctx.Reply("[StyxScrollInput] diag = " + Diag);
                return;
            }
            if (args.Length >= 1 && args[0].ToLowerInvariant() == "inspect")
            {
                InspectInputType();
                ctx.Reply("[StyxScrollInput] inspect dumped to log");
                return;
            }
            ctx.Reply(string.Format("[StyxScrollInput] tracking {0} player(s), diag={1}", _lastSlot.Count, Diag));
        });

        Log.Out("[StyxScrollInput] Loaded v0.1.2 -- mouse wheel maps to Jump/Crouch when a Styx menu is open");
    }

    static void InspectInputType()
    {
        var t = typeof(Styx.Ui.Input);
        var sb = new StringBuilder();
        sb.AppendLine("[StyxScrollInput] === Reflection on " + t.AssemblyQualifiedName + " ===");
        sb.AppendLine("-- Public+NonPublic Static Methods --");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                          .OrderBy(m => m.Name))
        {
            sb.Append("  ").Append(m.IsPublic ? "pub " : "int ").Append(m.ReturnType.Name).Append(" ").Append(m.Name).Append("(");
            sb.Append(string.Join(", ", m.GetParameters().Select(pi => pi.ParameterType.Name + " " + pi.Name)));
            sb.AppendLine(")");
        }
        sb.AppendLine("-- Public+NonPublic Static Fields --");
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                          .OrderBy(f => f.Name))
        {
            sb.Append("  ").Append(f.IsPublic ? "pub " : "int ").Append(f.FieldType.Name).Append(" ").Append(f.Name).AppendLine();
        }
        sb.AppendLine("-- Public+NonPublic Static Events --");
        foreach (var e in t.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                          .OrderBy(e => e.Name))
        {
            sb.Append("  ").Append(e.EventHandlerType.Name).Append(" ").Append(e.Name).AppendLine();
        }
        sb.AppendLine("-- Nested Types --");
        foreach (var n in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).OrderBy(n => n.Name))
        {
            sb.Append("  ").Append(n.Name).AppendLine();
        }
        Log.Out(sb.ToString());
    }

    public override void OnUnload()
    {
        _lastSlot.Clear();
    }

    // Reset slot tracking on (re)spawn so we don't dispatch a phantom
    // scroll on the first OnHoldingItemChanged after world entry.
    void OnPlayerSpawned(ClientInfo client, RespawnType reason, Vector3i pos)
    {
        if (client == null) return;
        var p = StyxCore.Player.FindByEntityId(client.entityId);
        if (p == null) return;
        _lastSlot[p.entityId] = p.inventory != null ? p.inventory.holdingItemIdx : 0;
    }

    void OnPlayerDisconnected(ClientInfo client, bool gameShuttingDown)
    {
        if (client == null) return;
        _lastSlot.Remove(client.entityId);
    }

    // === Slot-change handling (shared by both patch paths below) ===

    static void HandleSlotChange(EntityPlayer p, int newSlot, string source)
    {
        if (!_lastSlot.TryGetValue(p.entityId, out int prevSlot))
        {
            _lastSlot[p.entityId] = newSlot;
            if (Diag) Log.Out("[StyxScrollInput][{0}] prime player {1} -> slot {2}", source, p.entityId, newSlot);
            return;
        }
        _lastSlot[p.entityId] = newSlot;

        if (newSlot == prevSlot) return;

        bool menuOpen = StyxCore.Player.HasBuff(p, Styx.Ui.Input.ProbeBuff);

        if (Diag) Log.Out("[StyxScrollInput][{0}] eid {1}: {2} -> {3} (delta {4}) menuOpen={5}",
            source, p.entityId, prevSlot, newSlot, newSlot - prevSlot, menuOpen);

        if (!menuOpen) return;

        int delta = newSlot - prevSlot;

        // Adjacent or wrap-around -> scroll wheel. Anything else
        // (delta = 2..8 or -2..-8) is a hotkey number press, skip.
        //
        // Forward (next option):   delta = +1   OR delta = -9 (wrap 9 -> 0)
        // Backward (prev option):  delta = -1   OR delta = +9 (wrap 0 -> 9)
        Styx.Ui.StyxInputKind kind;
        if (delta == 1 || delta == -WrapDelta)
            kind = Styx.Ui.StyxInputKind.Jump;       // next
        else if (delta == -1 || delta == WrapDelta)
            kind = Styx.Ui.StyxInputKind.Crouch;     // prev
        else
        {
            if (Diag) Log.Out("[StyxScrollInput][{0}] non-adjacent delta -- treating as hotkey jump", source);
            return;
        }

        if (Diag) Log.Out("[StyxScrollInput][{0}] dispatch Styx.Ui.Input.Dispatch({1})", source, kind);
        Dispatch(p, kind);
    }

    // === Harmony patches ===
    //
    // Two paths because the doc claim that OnHoldingItemChanged fires
    // server-side may not hold on a dedicated server (player is remote).
    // NetPackageHoldingItem.ProcessPackage is the authoritative C->S
    // packet for slot changes -- patching there guarantees we see scroll
    // and hotkey events.

    [HarmonyPatch(typeof(EntityAlive), nameof(EntityAlive.OnHoldingItemChanged))]
    static class Patch_OnHoldingItemChanged
    {
        static void Postfix(EntityAlive __instance)
        {
            var p = __instance as EntityPlayer;
            if (p == null || p.inventory == null) return;
            HandleSlotChange(p, p.inventory.holdingItemIdx, "OHIC");
        }
    }

    [HarmonyPatch(typeof(NetPackageHoldingItem), "ProcessPackage")]
    static class Patch_NetPackageHoldingItem
    {
        static void Postfix(NetPackageHoldingItem __instance, World _world)
        {
            if (_world == null) return;
            // Sender is the ClientInfo who sent the packet. The packet
            // applies the new held-item state to that sender's entity.
            var sender = __instance?.Sender;
            if (sender == null) return;
            var ent = _world.GetEntity(sender.entityId) as EntityPlayer;
            if (ent == null || ent.inventory == null) return;
            HandleSlotChange(ent, ent.inventory.holdingItemIdx, "NPHI");
        }
    }
}
