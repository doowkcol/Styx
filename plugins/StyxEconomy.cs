// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// StyxEconomy -- per-player virtual currency (the bank).
//
// Pure server-side balance (no in-world item, no XML, no client mod).
// Stored as a JSON map of PlatformId -> long balance, atomic-write
// persisted via this.Data.
//
// This plugin is JUST the bank. It exposes the IEconomy service so
// other plugins can credit / debit players. Earn logic (zombie kills,
// quest rewards, harvest payouts, online stipend, etc.) lives in the
// separate StyxRewards plugin which consumes IEconomy via the Service
// Registry. Shop plugins, donor / Tebex bridges, quest tweaks etc.
// also call IEconomy directly -- keeps each consumer independent.
//
// HUD integration: every balance change pushes the new value to the
// player's `styx.eco.balance` cvar. The styxHud window has a row that
// reads `{cvar(styx.eco.balance:0)}` so the wallet shows live.
//
// Commands:
//   /balance               show your balance
//   /balance <player>      admin: show another player's balance
//   /pay <player> <amount> transfer credits to another online player
//   /eco grant <player> <amount> [reason]   admin: credit a player
//   /eco set   <player> <amount>            admin: set balance directly
//   /eco take  <player> <amount> [reason]   admin: debit a player

using System;
using System.Collections.Generic;
using System.IO;
using Styx;
using Styx.Data;
using Styx.Plugins;

[Info("StyxEconomy", "Doowkcol", "0.3.0")]
public class StyxEconomy : StyxPlugin, IEconomy
{
    public override string Description => "Per-player virtual currency wallet + IEconomy service";

    // ============================================================ Config

    public class Config
    {
        public string CurrencyName = "Credits";

        // Push balance to the styx.eco.balance cvar on every change so the
        // styxHud window can render it live.
        public bool DriveHudCvar = true;

        // Log every Credit/Debit to the server log -- useful while tuning.
        public bool LogTransactions = false;

        // Extra perm required for /eco wipe. Empty (default) = only
        // styx.eco.admin needed (current behaviour). Set to a NON-styx.*
        // perm name (e.g. "ops.wipe") to block visitors who got
        // styx.eco.admin via auth-0 implicit grants on a test server.
        // Operator manually grants the configured perm to themselves.
        public string WipeAdditionalPerm = "";
    }

    // ============================================================ Data

    public class WalletData
    {
        // Keyed by player PlatformId (e.g. "Steam_76561...", "EOS_000...").
        public Dictionary<string, long> Balances =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    }

    private Config _cfg;
    private DataStore<WalletData> _wallet;

    // ============================================================ Permissions

    private const string PermAdmin  = "styx.eco.admin";
    private const string PermPayCmd = "styx.eco.pay";

    // ============================================================ IEconomy

    public string CurrencyName => _cfg?.CurrencyName ?? "Credits";

    public long Balance(EntityPlayer player)
    {
        if (player == null) return 0;
        return Balance(StyxCore.Player.PlatformIdOf(player));
    }

    public long Balance(string platformId)
    {
        if (string.IsNullOrEmpty(platformId) || _wallet == null) return 0;
        return _wallet.Value.Balances.TryGetValue(platformId, out var v) ? v : 0L;
    }

    public void Credit(EntityPlayer player, long amount, string reason = null)
    {
        if (player == null || amount == 0) return;
        var pid = StyxCore.Player.PlatformIdOf(player);
        if (string.IsNullOrEmpty(pid)) return;
        long current = Balance(pid);
        long updated = current + amount;
        _wallet.Value.Balances[pid] = updated;
        _wallet.Save();
        if (_cfg.LogTransactions)
            Log.Out("[StyxEconomy] CREDIT {0} +{1} -> {2} ({3})", pid, amount, updated, reason ?? "?");
        PushHud(player, updated);
    }

    public void Debit(EntityPlayer player, long amount, string reason = null)
    {
        if (player == null || amount == 0) return;
        var pid = StyxCore.Player.PlatformIdOf(player);
        if (string.IsNullOrEmpty(pid)) return;
        long current = Balance(pid);
        long updated = current - amount;
        _wallet.Value.Balances[pid] = updated;
        _wallet.Save();
        if (_cfg.LogTransactions)
            Log.Out("[StyxEconomy] DEBIT  {0} -{1} -> {2} ({3})", pid, amount, updated, reason ?? "?");
        PushHud(player, updated);
    }

    public bool TryDebit(EntityPlayer player, long amount, string reason = null)
    {
        if (player == null || amount <= 0) return false;
        var pid = StyxCore.Player.PlatformIdOf(player);
        if (string.IsNullOrEmpty(pid)) return false;
        long current = Balance(pid);
        if (current < amount) return false;
        _wallet.Value.Balances[pid] = current - amount;
        _wallet.Save();
        if (_cfg.LogTransactions)
            Log.Out("[StyxEconomy] DEBIT  {0} -{1} -> {2} ({3})", pid, amount, current - amount, reason ?? "?");
        PushHud(player, current - amount);
        return true;
    }

    // Internal direct-set used by /eco set. Bypasses the +/- semantics of
    // Credit/Debit so admins can reset to an exact value.
    private void SetBalance(EntityPlayer player, long amount, string reason)
    {
        if (player == null) return;
        var pid = StyxCore.Player.PlatformIdOf(player);
        if (string.IsNullOrEmpty(pid)) return;
        _wallet.Value.Balances[pid] = amount;
        _wallet.Save();
        if (_cfg.LogTransactions)
            Log.Out("[StyxEconomy] SET    {0} = {1} ({2})", pid, amount, reason ?? "?");
        PushHud(player, amount);
    }

    private void PushHud(EntityPlayer p, long balance)
    {
        if (_cfg == null || !_cfg.DriveHudCvar || p == null) return;
        Styx.Ui.SetVar(p, "styx.eco.balance", balance);
    }

    // Wipe every wallet to zero. Backs up the existing wallet.json first
    // (atomic copy) so a misclick is recoverable. Returns count wiped.
    private int WipeAllBalances()
    {
        try
        {
            string backupName = "wallet.wiped-" + DateTime.UtcNow.ToString("yyyy-MM-ddTHHmmss") + ".json";
            string srcDir = Path.Combine(StyxCore.DataPath(), Name);
            Directory.CreateDirectory(srcDir);
            string srcFile = Path.Combine(srcDir, "wallet.json");
            if (File.Exists(srcFile))
                File.Copy(srcFile, Path.Combine(srcDir, backupName), overwrite: true);
        }
        catch (Exception e) { Log.Warning("[StyxEconomy] Wipe backup failed: " + e.Message); }

        int n = _wallet.Value.Balances.Count;
        _wallet.Value.Balances.Clear();
        _wallet.Save();

        // Reset live HUD for everyone online.
        var online = StyxCore.Player?.All();
        if (online != null)
            foreach (var p in online) PushHud(p, 0);

        return n;
    }

    // ============================================================ Lifecycle

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        _wallet = this.Data.Store<WalletData>("wallet");

        StyxCore.Perms.RegisterKnown(PermAdmin,
            "Admin: grant / take / set " + _cfg.CurrencyName + " balances", Name);
        StyxCore.Perms.RegisterKnown(PermPayCmd,
            "Use /pay to transfer " + _cfg.CurrencyName + " to other players", Name);

        // Service registry: publish so plugins (StyxRewards, StyxShop, etc.)
        // can resolve us via StyxCore.Services.Get<IEconomy>().
        StyxCore.Services.Publish<IEconomy>(this);

        // HUD label: currency name baked into runtime localization so the
        // styxHud window can paint "Credits:" / "Dukes:" / etc. Takes
        // effect on the NEXT server restart (label-bake lifecycle).
        Styx.Ui.Labels.Register(this, "styx_eco_currency", _cfg.CurrencyName);

        // Wipe the per-session sentinel cvar on every spawn so a stale
        // value from a prior install (without the plugin) doesn't leave
        // the HUD row visible after we unload.
        Styx.Ui.Ephemeral.Register("styx.eco.loaded", "styx.eco.balance");

        StyxCore.Commands.Register("balance",
            "Show your " + _cfg.CurrencyName + " balance -- /balance [player]",
            CmdBalance);
        StyxCore.Commands.Register("pay",
            "Transfer " + _cfg.CurrencyName + " to another player -- /pay <player> <amount>",
            CmdPay);
        StyxCore.Commands.Register("eco",
            "Economy admin -- /eco grant|take|set|wipe <args>",
            CmdEco);

        Log.Out("[StyxEconomy] Loaded v0.3.0 -- {0} player wallet(s), currency='{1}'",
            _wallet.Value.Balances.Count, _cfg.CurrencyName);
    }

    public override void OnUnload()
    {
        StyxCore.Services?.Unpublish<IEconomy>(this);

        // Hide the HUD row for everyone currently online (hot-unload path).
        var players = StyxCore.Player?.All();
        if (players != null)
            foreach (var p in players) Styx.Ui.SetVar(p, "styx.eco.loaded", 0f);
    }

    // ============================================================ Hooks

    void OnPlayerSpawned(ClientInfo client, RespawnType reason, Vector3i pos)
    {
        if (client == null) return;
        var p = StyxCore.Player.FindByEntityId(client.entityId);
        if (p == null) return;
        var pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid)) return;

        // Refresh HUD cvars on spawn -- handles reconnects + ephemeral wipes.
        // The "loaded" sentinel drives the visibility gate on the styxHud
        // Credits row.
        if (_cfg.DriveHudCvar)
        {
            Styx.Ui.SetVar(p, "styx.eco.loaded", 1f);
            PushHud(p, Balance(pid));
        }
    }

    // ============================================================ Commands

    private void CmdBalance(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }

        if (args.Length == 0)
        {
            var p = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
            if (p == null) { ctx.Reply("Player not found."); return; }
            long bal = Balance(p);
            ctx.Reply(string.Format("[ffcc00][Eco][-] Your balance: [ffffff]{0}[-] {1}",
                bal, _cfg.CurrencyName));
            return;
        }

        // /balance <player> -- admin only
        var actorId = StyxCore.Player.PlatformIdOf(StyxCore.Player.FindByEntityId(ctx.Client.entityId));
        if (!StyxCore.Perms.HasPermission(actorId, PermAdmin))
        { ctx.Reply("[ff6666]No permission.[-]"); return; }

        var target = StyxCore.Player.Find(string.Join(" ", args));
        if (target == null) { ctx.Reply("[ff6666]Player not found.[-]"); return; }
        long tBal = Balance(target);
        ctx.Reply(string.Format("[ffcc00][Eco][-] {0}: [ffffff]{1}[-] {2}",
            target.EntityName, tBal, _cfg.CurrencyName));
    }

    private void CmdPay(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }
        if (args.Length < 2) { ctx.Reply("Usage: /pay <player> <amount>"); return; }

        var sender = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
        if (sender == null) { ctx.Reply("Sender not found."); return; }
        var senderId = StyxCore.Player.PlatformIdOf(sender);

        if (!StyxCore.Perms.HasPermission(senderId, PermPayCmd))
        { ctx.Reply("[ff6666]No permission to use /pay.[-]"); return; }

        if (!long.TryParse(args[args.Length - 1], out long amount) || amount <= 0)
        { ctx.Reply("[ff6666]Amount must be a positive integer.[-]"); return; }

        // Everything before the amount is the player name.
        string targetName = string.Join(" ", args, 0, args.Length - 1);
        var target = StyxCore.Player.Find(targetName);
        if (target == null) { ctx.Reply("[ff6666]Recipient not found / not online.[-]"); return; }
        if (target.entityId == sender.entityId) { ctx.Reply("[ff6666]Can't pay yourself.[-]"); return; }

        if (!TryDebit(sender, amount, "pay " + target.EntityName))
        { ctx.Reply(string.Format("[ff6666]Insufficient {0} (balance: {1}).[-]",
            _cfg.CurrencyName, Balance(sender))); return; }

        Credit(target, amount, "pay from " + sender.EntityName);
        ctx.Reply(string.Format("[00ff66][Eco] Sent {0} {1} to {2}. New balance: {3}.[-]",
            amount, _cfg.CurrencyName, target.EntityName, Balance(sender)));
        Styx.Server.Whisper(target, string.Format(
            "[00ff66][Eco] Received {0} {1} from {2}. New balance: {3}.[-]",
            amount, _cfg.CurrencyName, sender.EntityName, Balance(target)));
    }

    private void CmdEco(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }
        var actor = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
        var actorId = StyxCore.Player.PlatformIdOf(actor);
        if (!StyxCore.Perms.HasPermission(actorId, PermAdmin))
        { ctx.Reply("[ff6666]No permission.[-]"); return; }

        if (args.Length < 1)
        { ctx.Reply("Usage: /eco grant|take|set <player> <amount> [reason]   |   /eco wipe confirm"); return; }

        string sub = args[0].ToLowerInvariant();

        // /eco wipe confirm  -- nukes all wallets, writes a backup first.
        if (sub == "wipe")
        {
            // Optional second-perm gate so test-server visitors with
            // auth-0 implicit styx.eco.admin can't trigger a wipe.
            if (!string.IsNullOrEmpty(_cfg.WipeAdditionalPerm) &&
                !StyxCore.Perms.HasPermission(actorId, _cfg.WipeAdditionalPerm))
            {
                ctx.Reply(string.Format(
                    "[ff6666]Wipe locked on this server (operator-only -- requires '{0}' perm).[-]",
                    _cfg.WipeAdditionalPerm));
                return;
            }
            if (args.Length < 2 || args[1].ToLowerInvariant() != "confirm")
            {
                ctx.Reply("[ffaa00]This wipes EVERY player's " + _cfg.CurrencyName + " balance. Re-run as: /eco wipe confirm[-]");
                return;
            }
            int n = WipeAllBalances();
            ctx.Reply(string.Format("[00ff66]Wiped {0} balance(s). Backup written.[-]", n));
            return;
        }

        if (args.Length < 3)
        { ctx.Reply("Usage: /eco grant|take|set <player> <amount> [reason]"); return; }

        string targetName = args[1];
        if (!long.TryParse(args[2], out long amount))
        { ctx.Reply("[ff6666]Amount must be an integer.[-]"); return; }
        string reason = args.Length > 3 ? string.Join(" ", args, 3, args.Length - 3) : sub;

        var target = StyxCore.Player.Find(targetName);
        if (target == null) { ctx.Reply("[ff6666]Player not found / not online.[-]"); return; }

        switch (sub)
        {
            case "grant":
                if (amount <= 0) { ctx.Reply("[ff6666]Grant amount must be positive.[-]"); return; }
                Credit(target, amount, reason);
                ctx.Reply(string.Format("[00ff66]Granted {0} {1} to {2}. New balance: {3}.[-]",
                    amount, _cfg.CurrencyName, target.EntityName, Balance(target)));
                break;
            case "take":
                if (amount <= 0) { ctx.Reply("[ff6666]Take amount must be positive.[-]"); return; }
                Debit(target, amount, reason);
                ctx.Reply(string.Format("[ffaa00]Took {0} {1} from {2}. New balance: {3}.[-]",
                    amount, _cfg.CurrencyName, target.EntityName, Balance(target)));
                break;
            case "set":
                SetBalance(target, amount, reason);
                ctx.Reply(string.Format("[00ff66]Set {0}'s balance to {1} {2}.[-]",
                    target.EntityName, amount, _cfg.CurrencyName));
                break;
            default:
                ctx.Reply("[ff6666]Unknown subcommand. Use grant | take | set.[-]");
                break;
        }
    }
}
