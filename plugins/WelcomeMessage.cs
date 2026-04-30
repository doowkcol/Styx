// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// WelcomeMessage — port of Doowkcol's Rust Carbon plugin to Styx / 7DTD V2.6.
//
// - Per-permission welcome messages defined in configs/WelcomeMessage.json
// - On player spawn: matching messages fire after WelcomeDelay seconds, ranked
//   high-to-low, optional depth limit, MessageDelay between each
// - Only first spawn per session gets the welcome (data/WelcomeMessage.seen.json
//   tracks seen platformIds for the current server-lifetime)
// - Admin commands: /wm list | reload | add <suffix>
//
// Exercises: Configs, Data (session persistence), Permissions,
// Hooks (OnPlayerSpawned), Scheduler (staggered messages), Server.Whisper.

using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Plugins;
using Styx.Scheduling;

[Info("WelcomeMessage", "Doowkcol", "0.1.0")]
public class WelcomeMessage : StyxPlugin
{
    public override string Description => "Permission-gated welcome messages on player spawn";

    private const string PermAdmin = "welcomemessage.admin";
    private const string PermUse = "welcomemessage.use";
    private const string PermPrefix = "welcomemessage.";

    public class PermissionMessage
    {
        public string Message = "Configure this welcome message.";
        public bool Enabled = false;
        public int Rank = 1;
    }

    public class Config
    {
        public float WelcomeDelaySeconds = 5f;
        public float MessageDelaySeconds = 2f;

        // If true, only players with welcomemessage.use get any welcome.
        // If false, everyone is eligible and per-message perms still apply.
        public bool RequireBaseUsePermission = false;

        // 0 = unlimited, otherwise send only the top-N ranked matching messages.
        public int MessageDepthLimit = 0;

        // Prefix every message with this (BBCode-capable).
        public string Prefix = "[55aaff][Welcome][-] ";

        public Dictionary<string, PermissionMessage> PermissionMessages =
            new Dictionary<string, PermissionMessage>(StringComparer.OrdinalIgnoreCase)
        {
            [PermPrefix + "vip"] = new PermissionMessage
            {
                Message = "Welcome VIP! Use [ffaa00]/kit vip[-] to claim your donor loadout.",
                Enabled = false,
                Rank = 20,
            },
            [PermPrefix + "discord"] = new PermissionMessage
            {
                Message = "Join our Discord at [55aaff]discord.gg/rekt[-] for updates and community chat.",
                Enabled = true,
                Rank = 5,
            },
            [PermPrefix + "default"] = new PermissionMessage
            {
                Message = "Welcome to the server! Type [ffaa00]/kit starter[-] to grab a starter kit.",
                Enabled = true,
                Rank = 1,
            },
        };
    }

    private Config _cfg;
    // In-memory only — resets when the plugin reloads or the server restarts,
    // which matches what "session" should mean. Don't use this.Data here, that
    // persists to disk and would make the welcome fire exactly once ever.
    private readonly HashSet<string> _welcomed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);

        StyxCore.Commands.Register("wm", "Welcome messages — /wm [list|reload|add <suffix>|test]", (ctx, args) =>
        {
            if (!RequireAdmin(ctx)) return;
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
            switch (sub)
            {
                case "list":   ShowList(ctx); break;
                case "reload": Reload(ctx); break;
                case "add":    AddPerm(ctx, args); break;
                case "test":   TestWelcome(ctx); break;
                default: ctx.Reply("Usage: /wm list | reload | add <suffix> | test"); break;
            }
        });

        Log.Out("[WelcomeMessage] Loaded — {0} message(s), delay {1}s.",
            _cfg.PermissionMessages.Count, _cfg.WelcomeDelaySeconds);
    }

    public override void OnUnload() { /* state auto-flushes on unload */ }

    // ---- Hook ----

    public void OnPlayerSpawned(ClientInfo ci, RespawnType reason)
    {
        if (ci == null) return;
        string platformId = ci.PlatformId?.CombinedString ?? "";
        if (string.IsNullOrEmpty(platformId)) return;
        // First spawn of this session only — respawns after death don't re-fire.
        if (_welcomed.Contains(platformId)) return;

        Scheduler.Once(_cfg.WelcomeDelaySeconds, () => DeliverFor(ci, platformId),
            name: "WelcomeMessage.deliver." + platformId);
    }

    // ---- core ----

    private void DeliverFor(ClientInfo ci, string platformId)
    {
        var player = StyxCore.Player.FindByEntityId(ci.entityId);
        if (player == null || !player.Spawned) return;

        if (_cfg.RequireBaseUsePermission && !StyxCore.Perms.HasPermission(platformId, PermUse))
        {
            Log.Out("[WelcomeMessage] {0}: lacks base {1}, skipping", ci.playerName, PermUse);
            return;
        }

        // Collect eligible messages.
        var matches = new List<(string perm, PermissionMessage msg)>();
        foreach (var kv in _cfg.PermissionMessages)
        {
            if (!kv.Value.Enabled) continue;
            if (!StyxCore.Perms.HasPermission(platformId, kv.Key)) continue;
            matches.Add((kv.Key, kv.Value));
        }
        if (matches.Count == 0)
        {
            Log.Out("[WelcomeMessage] {0}: no matching enabled messages (check /wm list + grants)", ci.playerName);
            return;
        }

        // Rank high→low, optional cap.
        matches = matches.OrderByDescending(m => m.msg.Rank).ToList();
        if (_cfg.MessageDepthLimit > 0 && matches.Count > _cfg.MessageDepthLimit)
            matches = matches.Take(_cfg.MessageDepthLimit).ToList();

        // Fire each with a stagger.
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            double delay = i * _cfg.MessageDelaySeconds;
            if (delay <= 0.01)
            {
                SendOne(player, m.msg.Message);
            }
            else
            {
                Scheduler.Once(delay, () =>
                {
                    var p = StyxCore.Player.FindByEntityId(ci.entityId);
                    if (p != null) SendOne(p, m.msg.Message);
                });
            }
        }

        _welcomed.Add(platformId);

        Log.Out("[WelcomeMessage] Sent {0} message(s) to {1}: [{2}]",
            matches.Count, ci.playerName,
            string.Join(", ", matches.Select(m => m.perm + " r=" + m.msg.Rank)));
    }

    private void SendOne(EntityPlayer player, string message)
    {
        Styx.Server.Whisper(player, (_cfg.Prefix ?? "") + message);
    }

    // ---- admin commands ----

    private void ShowList(Styx.Commands.CommandContext ctx)
    {
        if (_cfg.PermissionMessages.Count == 0) { ctx.Reply("No welcome messages configured."); return; }
        ctx.Reply("Welcome messages (" + _cfg.PermissionMessages.Count + "):");
        foreach (var kv in _cfg.PermissionMessages.OrderByDescending(x => x.Value.Rank))
        {
            string status = kv.Value.Enabled ? "[00ff66]on[-]" : "[888888]off[-]";
            ctx.Reply($"  r={kv.Value.Rank} {kv.Key} {status}");
        }
    }

    private void Reload(Styx.Commands.CommandContext ctx)
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        ctx.Reply("[00ff66]Reloaded[-] — " + _cfg.PermissionMessages.Count + " message(s).");
    }

    private void AddPerm(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (args.Length < 2) { ctx.Reply("Usage: /wm add <suffix>"); return; }
        string suffix = args[1].ToLowerInvariant().Trim();
        string full = PermPrefix + suffix;
        if (_cfg.PermissionMessages.ContainsKey(full))
        {
            ctx.Reply("Permission '" + full + "' already exists.");
            return;
        }
        _cfg.PermissionMessages[full] = new PermissionMessage
        {
            Message = "Welcome " + suffix + "! Configure this message in the config.",
            Enabled = false,
            Rank = 1,
        };
        StyxCore.Configs.Save(this, _cfg);
        ctx.Reply("[00ff66]Added[-] " + full + ". Edit configs/WelcomeMessage.json to set Enabled=true + message.");
    }

    /// <summary>Fire welcome flow for the caller immediately, bypassing the session-dedup.</summary>
    private void TestWelcome(Styx.Commands.CommandContext ctx)
    {
        var ci = ctx.Client;
        if (ci == null) { ctx.Reply("Test only works from an in-game client."); return; }
        string platformId = ci.PlatformId?.CombinedString ?? "";
        _welcomed.Remove(platformId);  // re-eligible
        DeliverFor(ci, platformId);
        ctx.Reply("[ffaa00]Test delivery triggered.[-]");
    }

    // ---- helpers ----

    private bool RequireAdmin(Styx.Commands.CommandContext ctx)
    {
        if (ctx.Client == null) return true;  // console
        string id = ctx.Client.PlatformId?.CombinedString;
        if (!string.IsNullOrEmpty(id) && StyxCore.Perms.HasPermission(id, PermAdmin)) return true;
        ctx.Reply("[ff6666]You don't have permission.[-]");
        return false;
    }
}
