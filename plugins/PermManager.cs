// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// PermManager — in-game chat UI around StyxCore.Perms. The same things
// `styx grant/revoke/usergroup/group/show` do in the F1 console, but as
// /perm in chat with BBCode output, player-name resolution, and pagination.
//
// Access: needs perm 'styx.perm.admin' for mutations (grant/revoke/group
// edits). Read-only queries (`/perm me`, `/perm groups`) are open.
//
// Player refs accept: platform id ("Steam_76561198XXXXXXXXX"), bare steam
// id ("76561198..."), in-game display name (case-insensitive prefix match,
// online first, then remembered players).

using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Permissions;
using Styx.Plugins;

[Info("PermManager", "Doowkcol", "0.1.0")]
public class PermManager : StyxPlugin
{
    public override string Description => "In-game chat UI for the Styx permission system";

    private const string PermAdmin = "styx.perm.admin";
    private const int PageSize = 15;

    private const string C_OK   = "[00ff66]";
    private const string C_INFO = "[55aaff]";
    private const string C_WARN = "[ffaa00]";
    private const string C_ERR  = "[ff6666]";
    private const string C_DIM  = "[888888]";

    public override void OnLoad()
    {
        StyxCore.Commands.Register("perm", "Permission manager — /perm help for subcommands", (ctx, args) =>
        {
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "me";
            switch (sub)
            {
                case "help":   ShowHelp(ctx); break;
                case "me":     ShowMe(ctx); break;
                case "show":   ShowUser(ctx, args); break;
                case "grant":  Grant(ctx, args); break;
                case "revoke": Revoke(ctx, args); break;
                case "addto":  AddToGroup(ctx, args); break;
                case "removefrom": RemoveFromGroup(ctx, args); break;
                case "group":  GroupSub(ctx, args); break;
                case "find":   Find(ctx, args); break;
                default: ctx.Reply(C_ERR + "Unknown subcommand. Try /perm help[-]"); break;
            }
        });

        StyxCore.Perms.RegisterKnown(PermAdmin,
            "Mutate the Styx permission system (grant/revoke/group)", Name);

        Log.Out("[PermManager] Loaded.");
    }

    public override void OnUnload()
    {
        StyxCore.Perms.UnregisterKnownByOwner(Name);
    }

    // ---------- Resolution ----------

    /// <summary>Resolve any string the user typed into a platform id like "Steam_76561198...".</summary>
    private string ResolvePlatformId(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        // Already a full platform id
        if (input.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("EOS_",   StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("Local_", StringComparison.OrdinalIgnoreCase))
            return input;

        // Bare numeric id — assume Steam
        if (input.Length >= 17 && input.All(char.IsDigit))
            return "Steam_" + input;

        // Online name match?
        var ci = StyxCore.Player.FindClient(input);
        if (ci != null)
        {
            var pid = ci.PlatformId?.CombinedString;
            if (!string.IsNullOrEmpty(pid)) return pid;
        }

        // Remembered player name match
        return StyxCore.Perms.FindPlayerIdByName(input);
    }

    // ---------- Commands ----------

    private void ShowHelp(Styx.Commands.CommandContext ctx)
    {
        ctx.Reply(C_INFO + "Permission manager:[-]");
        ctx.Reply("  /perm me                           — your groups and effective perms");
        ctx.Reply("  /perm show <player>                — another player's perms " + C_DIM + "(admin)[-]");
        ctx.Reply("  /perm grant <player> <perm>        " + C_DIM + "(admin)[-]");
        ctx.Reply("  /perm revoke <player> <perm>       " + C_DIM + "(admin)[-]");
        ctx.Reply("  /perm addto <player> <group>      " + C_DIM + "(admin)[-]");
        ctx.Reply("  /perm removefrom <player> <group> " + C_DIM + "(admin)[-]");
        ctx.Reply("  /perm group list                   — list all groups");
        ctx.Reply("  /perm group members <name>         — list members of a group");
        ctx.Reply("  /perm group create <name> [parent] " + C_DIM + "(admin)[-]");
        ctx.Reply("  /perm group delete <name>          " + C_DIM + "(admin)[-]");
        ctx.Reply("  /perm group grant <group> <perm>   " + C_DIM + "(admin)[-]");
        ctx.Reply("  /perm group revoke <group> <perm>  " + C_DIM + "(admin)[-]");
        ctx.Reply("  /perm find <pattern>               — search your effective perms");
        ctx.Reply(C_DIM + "<player> accepts name, Steam id, or full platform id[-]");
    }

    private void ShowMe(Styx.Commands.CommandContext ctx)
    {
        var pid = ctx.Client?.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid)) { ctx.Reply("Run this in-game."); return; }
        ShowUserById(ctx, pid, ctx.Client.playerName);
    }

    private void ShowUser(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (!RequireAdmin(ctx)) return;
        if (args.Length < 2) { ctx.Reply("Usage: /perm show <player>"); return; }
        var pid = ResolvePlatformId(args[1]);
        if (pid == null) { ctx.Reply(C_ERR + "Can't find player '" + args[1] + "'[-]"); return; }

        var pd = StyxCore.Perms.GetPlayer(pid);
        string name = pd?.LastSeenName ?? args[1];
        ShowUserById(ctx, pid, name);
    }

    private void ShowUserById(Styx.Commands.CommandContext ctx, string pid, string displayName)
    {
        var groups = StyxCore.Perms.GetPlayerGroups(pid);
        var effective = StyxCore.Perms.GetEffectivePermissions(pid);
        var pd = StyxCore.Perms.GetPlayer(pid);

        ctx.Reply(C_INFO + "Player: " + displayName + C_DIM + "  [" + pid + "][-]");
        ctx.Reply("  Groups: " + (groups.Count == 0 ? C_DIM + "(default only)[-]" : string.Join(", ", groups)));

        if (pd != null)
        {
            if (pd.Grants.Count > 0)
                ctx.Reply("  Direct grants (" + pd.Grants.Count + "): " + string.Join(", ", pd.Grants.OrderBy(s => s)));
            if (pd.Revokes.Count > 0)
                ctx.Reply("  Direct revokes (" + pd.Revokes.Count + "): " + string.Join(", ", pd.Revokes.OrderBy(s => s)));
        }

        if (effective.Count == 0)
        {
            ctx.Reply("  " + C_DIM + "(no effective permissions)[-]");
            return;
        }
        ctx.Reply("  Effective (" + effective.Count + "):");
        foreach (var chunk in ChunkLines(effective.OrderBy(s => s), PageSize))
            ctx.Reply("    " + chunk);
    }

    /// <summary>
    /// Privilege-escalation guard. The actor (chat sender) cannot mutate
    /// perms on a target that has MORE authority than themselves (lower
    /// auth level). Whispers a clear error if the guard fails.
    /// </summary>
    private bool RequireAuthority(Styx.Commands.CommandContext ctx, string targetPid)
    {
        var actorPid = ctx.Client?.PlatformId?.CombinedString;
        if (StyxCore.Perms.CanActorMutateTarget(actorPid, targetPid)) return true;

        int actorAuth  = StyxCore.Perms.GetAuthLevel(actorPid);
        int targetAuth = StyxCore.Perms.GetAuthLevel(targetPid);
        ctx.Reply(C_ERR + "Refused — target has more authority than you " +
                 "(your auth " + actorAuth + " > target auth " + targetAuth +
                 "; lower number = more authority).[-]");
        return false;
    }

    private void Grant(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (!RequireAdmin(ctx)) return;
        if (args.Length < 3) { ctx.Reply("Usage: /perm grant <player> <perm>"); return; }
        var pid = ResolvePlatformId(args[1]);
        if (pid == null) { ctx.Reply(C_ERR + "Can't find player '" + args[1] + "'[-]"); return; }
        if (!RequireAuthority(ctx, pid)) return;
        string perm = args[2].ToLowerInvariant();
        bool added = StyxCore.Perms.GrantToPlayer(pid, perm);
        ctx.Reply(added
            ? C_OK + "Granted '" + perm + "' to " + (StyxCore.Perms.GetPlayer(pid)?.LastSeenName ?? pid) + "[-]"
            : C_WARN + "Already had '" + perm + "'[-]");
    }

    private void Revoke(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (!RequireAdmin(ctx)) return;
        if (args.Length < 3) { ctx.Reply("Usage: /perm revoke <player> <perm>"); return; }
        var pid = ResolvePlatformId(args[1]);
        if (pid == null) { ctx.Reply(C_ERR + "Can't find player '" + args[1] + "'[-]"); return; }
        if (!RequireAuthority(ctx, pid)) return;
        string perm = args[2].ToLowerInvariant();
        bool added = StyxCore.Perms.RevokeFromPlayer(pid, perm);
        ctx.Reply(added
            ? C_OK + "Revoked '" + perm + "' from " + (StyxCore.Perms.GetPlayer(pid)?.LastSeenName ?? pid) + "[-]"
            : C_WARN + "Already revoked '" + perm + "'[-]");
    }

    private void AddToGroup(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (!RequireAdmin(ctx)) return;
        if (args.Length < 3) { ctx.Reply("Usage: /perm addto <player> <group>"); return; }
        var pid = ResolvePlatformId(args[1]);
        if (pid == null) { ctx.Reply(C_ERR + "Can't find player '" + args[1] + "'[-]"); return; }
        if (!RequireAuthority(ctx, pid)) return;
        string group = args[2];
        if (!StyxCore.Perms.GroupExists(group)) { ctx.Reply(C_ERR + "Group '" + group + "' does not exist[-]"); return; }
        bool added = StyxCore.Perms.AddPlayerToGroup(pid, group);
        ctx.Reply(added
            ? C_OK + "Added to group '" + group + "'[-]"
            : C_WARN + "Already in group[-]");
    }

    private void RemoveFromGroup(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (!RequireAdmin(ctx)) return;
        if (args.Length < 3) { ctx.Reply("Usage: /perm removefrom <player> <group>"); return; }
        var pid = ResolvePlatformId(args[1]);
        if (pid == null) { ctx.Reply(C_ERR + "Can't find player '" + args[1] + "'[-]"); return; }
        if (!RequireAuthority(ctx, pid)) return;
        string group = args[2];
        bool removed = StyxCore.Perms.RemovePlayerFromGroup(pid, group);
        ctx.Reply(removed
            ? C_OK + "Removed from group '" + group + "'[-]"
            : C_WARN + "Wasn't in group[-]");
    }

    // ---------- Group subcommands ----------

    private void GroupSub(Styx.Commands.CommandContext ctx, string[] args)
    {
        string op = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
        switch (op)
        {
            case "list":    GroupList(ctx); break;
            case "members": GroupMembers(ctx, args); break;
            case "create":  GroupCreate(ctx, args); break;
            case "delete":  GroupDelete(ctx, args); break;
            case "grant":   GroupGrant(ctx, args); break;
            case "revoke":  GroupRevoke(ctx, args); break;
            default: ctx.Reply(C_ERR + "Unknown — /perm group list|members|create|delete|grant|revoke[-]"); break;
        }
    }

    private void GroupList(Styx.Commands.CommandContext ctx)
    {
        var groups = StyxCore.Perms.GetAllGroups();
        ctx.Reply(C_INFO + "Groups (" + groups.Count + "):[-]");
        foreach (var g in groups)
        {
            string parent = g.HasParent ? C_DIM + " < " + g.Parent + "[-]" : "";
            ctx.Reply("  " + g.Name + parent + C_DIM + "  " + g.Perms.Count + " perm(s)[-]");
        }
    }

    private void GroupMembers(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (args.Length < 3) { ctx.Reply("Usage: /perm group members <name>"); return; }
        string group = args[2];
        if (!StyxCore.Perms.GroupExists(group)) { ctx.Reply(C_ERR + "Group '" + group + "' does not exist[-]"); return; }
        var members = StyxCore.Perms.GetPlayersInGroup(group);
        ctx.Reply(C_INFO + "Members of '" + group + "' (" + members.Count + "):[-]");
        foreach (var kv in members.OrderBy(m => m.Value.LastSeenName ?? m.Key))
            ctx.Reply("  " + (kv.Value.LastSeenName ?? "?") + C_DIM + "  [" + kv.Key + "][-]");
        if (members.Count == 0) ctx.Reply("  " + C_DIM + "(none)[-]");
    }

    private void GroupCreate(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (!RequireAdmin(ctx)) return;
        if (args.Length < 3) { ctx.Reply("Usage: /perm group create <name> [parent]"); return; }
        string name = args[2];
        string parent = args.Length > 3 ? args[3] : null;
        if (parent != null && !StyxCore.Perms.GroupExists(parent)) { ctx.Reply(C_ERR + "Parent '" + parent + "' does not exist[-]"); return; }
        bool ok = StyxCore.Perms.CreateGroup(name, parent);
        ctx.Reply(ok ? C_OK + "Created group '" + name + "'[-]" : C_WARN + "Group already exists[-]");
    }

    private void GroupDelete(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (!RequireAdmin(ctx)) return;
        if (args.Length < 3) { ctx.Reply("Usage: /perm group delete <name>"); return; }
        bool ok = StyxCore.Perms.DeleteGroup(args[2]);
        ctx.Reply(ok ? C_OK + "Deleted[-]" : C_ERR + "Not found or protected[-]");
    }

    private void GroupGrant(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (!RequireAdmin(ctx)) return;
        if (args.Length < 4) { ctx.Reply("Usage: /perm group grant <group> <perm>"); return; }
        string g = args[2];
        if (!StyxCore.Perms.GroupExists(g)) { ctx.Reply(C_ERR + "Group '" + g + "' does not exist[-]"); return; }
        bool ok = StyxCore.Perms.GrantToGroup(g, args[3].ToLowerInvariant());
        ctx.Reply(ok ? C_OK + "Granted to group[-]" : C_WARN + "Already had it[-]");
    }

    private void GroupRevoke(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (!RequireAdmin(ctx)) return;
        if (args.Length < 4) { ctx.Reply("Usage: /perm group revoke <group> <perm>"); return; }
        string g = args[2];
        if (!StyxCore.Perms.GroupExists(g)) { ctx.Reply(C_ERR + "Group '" + g + "' does not exist[-]"); return; }
        bool ok = StyxCore.Perms.RevokeFromGroup(g, args[3].ToLowerInvariant());
        ctx.Reply(ok ? C_OK + "Removed from group[-]" : C_WARN + "Group did not have it[-]");
    }

    // ---------- Find ----------

    private void Find(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (args.Length < 2) { ctx.Reply("Usage: /perm find <pattern>"); return; }
        string pattern = args[1].ToLowerInvariant();
        var pid = ctx.Client?.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid)) { ctx.Reply("Run this in-game."); return; }
        var effective = StyxCore.Perms.GetEffectivePermissions(pid);
        var hits = effective.Where(p => p.ToLowerInvariant().Contains(pattern)).OrderBy(s => s).ToList();
        if (hits.Count == 0) { ctx.Reply(C_DIM + "No matches[-]"); return; }
        ctx.Reply(C_INFO + "Matches (" + hits.Count + "):[-]");
        foreach (var chunk in ChunkLines(hits, PageSize)) ctx.Reply("  " + chunk);
    }

    // ---------- helpers ----------

    private bool RequireAdmin(Styx.Commands.CommandContext ctx)
    {
        if (ctx.Client == null) return true;  // console bypass
        string pid = ctx.Client.PlatformId?.CombinedString;
        if (!string.IsNullOrEmpty(pid) && StyxCore.Perms.HasPermission(pid, PermAdmin)) return true;
        ctx.Reply(C_ERR + "You don't have permission. (needs " + PermAdmin + ")[-]");
        return false;
    }

    /// <summary>Groups a flat list of strings into comma-joined rows of <paramref name="perRow"/> items.</summary>
    private static IEnumerable<string> ChunkLines(IEnumerable<string> items, int perRow)
    {
        var buffer = new List<string>();
        foreach (var it in items)
        {
            buffer.Add(it);
            if (buffer.Count >= perRow)
            {
                yield return string.Join(", ", buffer);
                buffer.Clear();
            }
        }
        if (buffer.Count > 0) yield return string.Join(", ", buffer);
    }
}
