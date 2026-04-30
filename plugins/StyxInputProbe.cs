// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// StyxInputProbe — demo of Styx.Ui.Input (v0.6.3 framework subsystem).
//
// Uses the hook-bus pattern: declaring OnPlayerInput auto-subscribes us to
// every input event the framework dispatches. We receive events for every
// player whose inputs we've acquired via Ui.Input.Acquire.
//
// Commands:
//   /input on          start receiving input events
//   /input off         stop
//   /input status      report buff + active-consumer state

using System.Collections.Generic;
using Styx;
using Styx.Plugins;

[Info("StyxInputProbe", "Doowkcol", "0.3.0")]
public class StyxInputProbe : StyxPlugin
{
    public override string Description => "Demo of Styx.Ui.Input event subsystem";

    private readonly HashSet<int> _armed = new HashSet<int>();

    private static readonly Dictionary<Styx.Ui.StyxInputKind, string> Labels = new Dictionary<Styx.Ui.StyxInputKind, string>
    {
        { Styx.Ui.StyxInputKind.Jump,            "Jump"            },
        { Styx.Ui.StyxInputKind.Crouch,          "Crouch"          },
        { Styx.Ui.StyxInputKind.Reload,          "Reload"          },
        { Styx.Ui.StyxInputKind.PrimaryAction,   "Primary (LMB)"   },
        { Styx.Ui.StyxInputKind.SecondaryAction, "Secondary (RMB)" },
    };

    public override void OnLoad()
    {
        StyxCore.Commands.Register("input", "Input probe — /input [on|off|status]", (ctx, args) =>
        {
            if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }
            var p = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
            if (p == null) { ctx.Reply("Player not found."); return; }

            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "on";
            switch (sub)
            {
                case "on":
                    Styx.Ui.Input.Acquire(p, Name);
                    _armed.Add(p.entityId);
                    ctx.Reply("[00ff66]Input probe armed — try jumping, crouching, LMB/RMB.[-]");
                    break;
                case "off":
                    Styx.Ui.Input.Release(p, Name);
                    _armed.Remove(p.entityId);
                    ctx.Reply("[ffaa00]Input probe released.[-]");
                    break;
                case "status":
                    ctx.Reply(string.Format("armed: {0} buff: {1}",
                        _armed.Contains(p.entityId),
                        StyxCore.Player.HasBuff(p, Styx.Ui.Input.ProbeBuff)));
                    break;
                default:
                    ctx.Reply("Usage: /input on | off | status");
                    break;
            }
        });

        Log.Out("[StyxInputProbe] Loaded v0.3.0 — subscribed to Styx.Ui.Input via OnPlayerInput hook");
    }

    public override void OnUnload()
    {
        foreach (var eid in _armed)
        {
            var p = StyxCore.Player.FindByEntityId(eid);
            if (p != null) Styx.Ui.Input.Release(p, Name);
        }
        _armed.Clear();
    }

    // Auto-wired by the hook bus. Fires for every player we hold a claim on.
    void OnPlayerInput(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (p == null || !_armed.Contains(p.entityId)) return;
        string label = Labels.TryGetValue(kind, out var s) ? s : "unknown(" + kind + ")";
        Styx.Server.Whisper(p, "[00ff66][Styx input] " + label + "[-]");
    }
}
