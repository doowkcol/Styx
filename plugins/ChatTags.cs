// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// ChatTags — group-priority chat tag prefix.
//
// Reads each speaker's group memberships from StyxCore.Perms.GetGroupTag and
// rebroadcasts their chat as "[Tag] PlayerName: message" with the group's
// configured BBCode colour. Highest-priority group wins (configured per-group
// via Priority + ChatTag + ChatTagColor in data/permissions.json).
//
// Default groups ship with sensible tags:
//   admin   → [Admin] (red,    priority 100)
//   vip     → [VIP]   (gold,   priority  50)
//   default → no tag  (priority   0)
//
// Edit via PermEditor UI (next round) or directly in permissions.json.
//
// Hook semantics: returning non-null from OnChatMessage cancels vanilla chat,
// so we MUST then rebroadcast manually. Slash commands are passed through
// unmodified — they must run their existing dispatch path in StyxCore.

using System;
using Styx;
using Styx.Plugins;

[Info("ChatTags", "Doowkcol", "0.1.0")]
public class ChatTags : StyxPlugin
{
    public override string Description => "Group-priority [Tag] prefix on player chat";

    public override void OnLoad()
    {
        Log.Out("[ChatTags] Loaded v0.1.0 — tags from group config (Priority + ChatTag + ChatTagColor).");
    }

    /// <summary>
    /// Hook handler. Returning a non-null object cancels vanilla chat
    /// (StopHandlersAndVanilla in StyxCore.OnChatMessage). Returning null
    /// passes through unchanged.
    /// </summary>
    object OnChatMessage(ClientInfo client, string message, EChatType chatType)
    {
        // Don't touch slash commands — they need to flow to the framework's
        // command dispatcher in StyxCore.OnChatMessage (which runs AFTER us).
        if (client == null || string.IsNullOrEmpty(message)) return null;
        if (message[0] == '/') return null;

        string pid = client.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid)) return null;

        var (tag, color) = StyxCore.Perms.GetGroupTag(pid);
        if (string.IsNullOrEmpty(tag)) return null;  // No tag → vanilla chat is fine

        // Build "[ColorBBCode][Tag][-] PlayerName: message". The BBCode color
        // wraps the tag only — name + message keep vanilla styling.
        string playerName = client.playerName ?? "?";
        string colored = string.IsNullOrEmpty(color)
            ? tag
            : "[" + color + "]" + tag + "[-]";
        string formatted = colored + " " + playerName + ": " + message;

        try
        {
            // Rebroadcast as a server-sourced chat — matches the positional
            // signature CommandContext.Reply uses (which we know compiles).
            // V2.6's ChatMessageServer parameter names differ from earlier
            // versions, so positional args are the safe call form.
            //
            // Args in order:
            //   1. ClientInfo  — null = "no sender ClientInfo" → engine
            //                    won't prepend a playerName
            //   2. EChatType   — preserve original (Global/Local/etc.)
            //   3. int         — sender entityId; -1 = server
            //   4. string      — pre-formatted message body
            //   5. List<int>?  — recipients; null = broadcast to all
            //   6. EMessageSender — Server (V2.6 has no .Player enum value)
            //   7. BbCodeSupportMode — Supported, so [color] tags render
            GameManager.Instance.ChatMessageServer(
                null,
                chatType,
                -1,
                formatted,
                null,
                EMessageSender.Server,
                GeneratedTextManager.BbCodeSupportMode.Supported);
        }
        catch (Exception e)
        {
            Log.Warning("[ChatTags] Rebroadcast failed: " + e.Message);
            return null;  // Fall back to vanilla — better than dropping the message
        }

        // Mirror to server log so admins watching console see the same as players.
        Log.Out("[Chat:{0}] {1} {2}: {3}", chatType, tag, playerName, message);

        return true;  // Cancel vanilla chat — we've already rebroadcast
    }
}
