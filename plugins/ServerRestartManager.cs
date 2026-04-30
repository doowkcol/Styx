// SPDX-License-Identifier: LicenseRef-Styx-Plugin-Restricted
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood). All rights reserved.
//
// This plugin is part of the Styx Restricted plugin set.
// Personal use on a single server you operate is permitted.
// Modification for personal use is permitted.
//
// Redistribution, resale, or use as part of a paid hosting /
// managed-service offering requires a separate commercial licence.
// Contact: jacklockwood@outlook.com
//
// See LICENSE-PLUGINS at the repo root for full terms.
//

// ServerRestartManager — port of the Carbon/Rust plugin to 7DTD via Styx.
//
// Schedules one or more daily restart times. At each configured time:
//   1. Broadcasts warning messages at T-20m / T-15m / T-10m / T-5m (configurable).
//   2. Final per-second countdown for the last 30 seconds.
//   3. Executes the "shutdown" console command to cleanly stop the server
//      (the hosting wrapper / systemd / launcher is expected to restart it).
//
// All timing runs through Styx.Scheduling.Scheduler which pumps off the
// GameUpdate tick. Permissions gate the admin command.
//
// No UI — 7DTD has no server-side CUI equivalent. Warnings come as chat
// broadcasts (BBCode colours supported) and the console shows progress.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Styx;
using Styx.Plugins;
using Styx.Scheduling;

[Info("ServerRestartManager", "Doowkcol", "0.1.0")]
public class ServerRestartManager : StyxPlugin
{
    public override string Description => "Scheduled daily server restarts with warnings";

    private const string Perm = "styx.restart.admin";

    private Configuration _cfg;
    private DateTime _nextRestart;
    private bool _restartInProgress;
    private int _lastAnnouncedMinute = -1;
    private int _lastAnnouncedSecond = -1;
    private TimerHandle _tick;

    // ---- Admin UI state ----
    private const int UiRowCount = 4;
    private const double ConfirmWindowSec = 5.0;
    private static readonly int[] WipeCyclePresets = { 1, 3, 7, 14, 21, 28, 30, 60, 90 };

    private readonly System.Collections.Generic.HashSet<int> _open =
        new System.Collections.Generic.HashSet<int>();
    // Pending "trigger restart now" two-tap confirms (entityId → unix seconds of first tap).
    private readonly System.Collections.Generic.Dictionary<int, double> _confirmPending =
        new System.Collections.Generic.Dictionary<int, double>();

    // ---- Config ----

    public class Configuration
    {
        [JsonProperty("Enabled")]
        public bool Enabled { get; set; } = true;

        [JsonProperty("Restart Times (24h)")]
        public List<RestartTime> RestartTimes { get; set; } = new List<RestartTime>
        {
            new RestartTime(6, 0),
            new RestartTime(14, 0),
            new RestartTime(22, 0),
        };

        [JsonProperty("Warning Start Minutes")]
        public int WarningStartMinutes { get; set; } = 20;

        [JsonProperty("Warning Messages (minute -> message, {0} = minutes left)")]
        public Dictionary<int, string> WarningMessages { get; set; } = new Dictionary<int, string>
        {
            [20] = "[ff3030]Server restart in [ffffff]{0}[ff3030] minutes.[-]",
            [15] = "[ff3030]Server restart in [ffffff]{0}[ff3030] minutes.[-]",
            [10] = "[ff3030]Server restart in [ffffff]{0}[ff3030] minutes.[-]",
            [5]  = "[ff3030]Server restart in [ffffff]{0}[ff3030] minutes — wrap up![-]",
            [1]  = "[ff0000]Server restart in 1 minute![-]",
        };

        [JsonProperty("Final Countdown Start (seconds)")]
        public int FinalCountdownStart { get; set; } = 30;

        [JsonProperty("Final Countdown Every (seconds)")]
        public int FinalCountdownEvery { get; set; } = 5; // 30, 25, 20, ... plus always the last 5.

        [JsonProperty("Shutdown Command")]
        public string ShutdownCommand { get; set; } = "shutdown";

        [JsonProperty("Shutdown Broadcast")]
        public string ShutdownBroadcast { get; set; } = "[ff0000]Server restarting NOW![-]";

        // ---- Wipe schedule (last-wipe + cycle model) ----
        // Drives the StyxHud "Next wipe: Xd Yh" countdown. Admin sets
        // LastWipeUnix when they perform a wipe (chat command or UI button)
        // and the plugin auto-computes next wipe = last + WipeCycleDays.

        [JsonProperty("Last Wipe (Unix UTC seconds)")]
        public long LastWipeUnix { get; set; } = 0;  // 0 = auto-init to first-run time

        [JsonProperty("Wipe Cycle Days")]
        public int WipeCycleDays { get; set; } = 7;  // 0 = no countdown (manual wipes)

        [JsonProperty("HUD Restart Warning Window Minutes")]
        public int HudRestartWarningMinutes { get; set; } = 60;
    }

    public class RestartTime
    {
        public int Hour { get; set; }
        public int Minute { get; set; }
        public RestartTime() { }
        public RestartTime(int h, int m) { Hour = h; Minute = m; }
        public override string ToString() => $"{Hour:D2}:{Minute:D2}";
    }

    // ---- Lifecycle ----

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Configuration>(this);
        SanitizeConfig();

        // First-run: if LastWipeUnix was never set, anchor the cycle to "now"
        // so the wipe countdown displays sensibly from day one. Admin can
        // adjust later via the UI's "Reset wipe to now" action when they
        // perform an actual wipe.
        if (_cfg.LastWipeUnix <= 0)
        {
            _cfg.LastWipeUnix = UnixNow();
            StyxCore.Configs.Save(this, _cfg);
            Log.Out("[ServerRestartManager] LastWipeUnix initialised to now ({0})", _cfg.LastWipeUnix);
        }

        ComputeNextRestart();
        Log.Out("[ServerRestartManager] Next restart at {0:yyyy-MM-dd HH:mm}", _nextRestart);

        _tick = Scheduler.Every(1.0, Tick, name: "ServerRestartManager.tick");

        StyxCore.Commands.Register("srm", "ServerRestartManager admin — subcommands: status, times, reload", (ctx, args) =>
        {
            if (!RequireAdmin(ctx)) return;

            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            switch (sub)
            {
                case "status": ShowStatus(ctx); break;
                case "times": ShowTimes(ctx); break;
                case "reload":
                    _cfg = StyxCore.Configs.Load<Configuration>(this);
                    SanitizeConfig();
                    ComputeNextRestart();
                    _lastAnnouncedMinute = -1;
                    _lastAnnouncedSecond = -1;
                    ctx.Reply("Config reloaded. Next restart: " + _nextRestart.ToString("yyyy-MM-dd HH:mm"));
                    break;
                default:
                    ctx.Reply("Usage: /srm status | times | reload");
                    break;
            }
        });

        StyxCore.Perms.RegisterKnown(Perm,
            "Run /srm admin commands + open the SRM admin UI", Name);

        // HUD cvars consumed by StyxHud — register as ephemeral so they
        // get cleared on respawn (avoids stale panel readings in the 1s
        // window before the next tick repopulates them).
        Styx.Ui.Ephemeral.Register(
            "styx.srm.wipe_d", "styx.srm.wipe_h",
            "styx.srm.restart_d", "styx.srm.restart_h",
            "styx.srm.restart_m", "styx.srm.restart_s",
            "styx.srm.restart_warning",
            // UI-side cvars for the admin SRM menu (per-player render state)
            "styx.srm.open", "styx.srm.sel",
            "styx.srm.enabled", "styx.srm.cycle",
            "styx.srm.last_wipe_d", "styx.srm.confirm_pending");

        // Launcher entry — admin only. Same perm as the chat command.
        Styx.Ui.Menu.Register(this, "Server Restart Manager", OpenFor, permission: Perm);
    }

    public override void OnUnload()
    {
        _tick?.Destroy();
        _tick = null;
        // Close any open admin-UI sessions cleanly.
        foreach (var eid in _open)
        {
            var p = StyxCore.Player.FindByEntityId(eid);
            if (p != null)
            {
                Styx.Ui.SetVar(p, "styx.srm.open", 0f);
                Styx.Ui.Input.Release(p, Name);
            }
        }
        _open.Clear();
        _confirmPending.Clear();
        Styx.Ui.Menu.UnregisterAll(this);
        StyxCore.Perms.UnregisterKnownByOwner(Name);
    }

    // ---- Tick ----

    private void Tick()
    {
        if (_restartInProgress) return;

        var now = DateTime.Now;

        // Push HUD cvars on every tick — independent of the Enabled flag,
        // so the StyxHud panel can render even when restarts are paused.
        // (When disabled we just push the wipe countdown and zero the
        // restart-related cvars so the warning section auto-hides.)
        PushHudCvars(now);

        if (!_cfg.Enabled) return;

        var remaining = _nextRestart - now;

        if (remaining.TotalSeconds <= 0)
        {
            TriggerRestart();
            return;
        }

        // Minute-granularity warnings.
        if (remaining.TotalSeconds > _cfg.FinalCountdownStart)
        {
            int minutes = (int)Math.Ceiling(remaining.TotalMinutes);
            if (minutes != _lastAnnouncedMinute && minutes <= _cfg.WarningStartMinutes)
            {
                if (_cfg.WarningMessages != null &&
                    _cfg.WarningMessages.TryGetValue(minutes, out var tmpl))
                {
                    Styx.Server.Broadcast(string.Format(tmpl, minutes));
                }
                _lastAnnouncedMinute = minutes;
            }
            return;
        }

        // Final countdown — broadcast at configured step AND every second for last 5s.
        int seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        if (seconds == _lastAnnouncedSecond) return;
        _lastAnnouncedSecond = seconds;

        bool shouldAnnounce =
            seconds <= 5 ||
            (_cfg.FinalCountdownEvery > 0 && seconds % _cfg.FinalCountdownEvery == 0);

        if (shouldAnnounce)
        {
            Styx.Server.Broadcast($"[ff3030]Server restart in [ffffff]{seconds}[ff3030] second(s).[-]");
        }
    }

    private void TriggerRestart()
    {
        _restartInProgress = true;
        Styx.Server.Broadcast(_cfg.ShutdownBroadcast ?? "Server restarting.");
        Log.Out("[ServerRestartManager] Restart time reached. Executing: {0}", _cfg.ShutdownCommand);

        // Give chat a couple of seconds to propagate before shutdown.
        Scheduler.Once(2.0, () =>
        {
            Styx.Server.ExecConsole(_cfg.ShutdownCommand ?? "shutdown");
        }, name: "ServerRestartManager.shutdown");
    }

    // ---- Schedule ----

    private void ComputeNextRestart()
    {
        var now = DateTime.Now;
        DateTime best = DateTime.MaxValue;
        foreach (var rt in _cfg.RestartTimes)
        {
            var today = new DateTime(now.Year, now.Month, now.Day, rt.Hour, rt.Minute, 0);
            var candidate = today <= now ? today.AddDays(1) : today;
            if (candidate < best) best = candidate;
        }
        _nextRestart = best == DateTime.MaxValue
            ? now.AddDays(1) // no restart times configured
            : best;
    }

    /// <summary>Compute next wipe time from LastWipeUnix + WipeCycleDays.
    /// Returns DateTime.MaxValue if cycle is disabled (cycle=0) or wipe was
    /// never set (LastWipeUnix=0). Caller treats max as "no countdown".</summary>
    private DateTime ComputeNextWipe(DateTime now)
    {
        if (_cfg.LastWipeUnix <= 0 || _cfg.WipeCycleDays <= 0) return DateTime.MaxValue;
        var lastWipeUtc = DateTimeOffset.FromUnixTimeSeconds(_cfg.LastWipeUnix).UtcDateTime;
        var lastWipeLocal = lastWipeUtc.ToLocalTime();
        return lastWipeLocal.AddDays(_cfg.WipeCycleDays);
    }

    /// <summary>Returns now as a Unix UTC seconds timestamp.</summary>
    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Broadcast HUD-display cvars to every online player. StyxHud
    /// reads these to render the wipe countdown + restart warning section.
    /// Cheap — runs at 1Hz alongside the existing restart tick.</summary>
    private void PushHudCvars(DateTime now)
    {
        // Restart countdown — only meaningful when Enabled.
        if (_cfg.Enabled)
        {
            var rrem = _nextRestart - now;
            int rd = Math.Max(0, (int)rrem.TotalDays);
            int rh = Math.Max(0, rrem.Hours);
            int rm = Math.Max(0, rrem.Minutes);
            int rs = Math.Max(0, rrem.Seconds);
            int totalMin = (int)Math.Ceiling(rrem.TotalMinutes);
            int warning = (totalMin > 0 && totalMin <= _cfg.HudRestartWarningMinutes) ? 1 : 0;

            Styx.Ui.SetVarAll("styx.srm.restart_d", rd);
            Styx.Ui.SetVarAll("styx.srm.restart_h", rh);
            Styx.Ui.SetVarAll("styx.srm.restart_m", rm);
            Styx.Ui.SetVarAll("styx.srm.restart_s", rs);
            Styx.Ui.SetVarAll("styx.srm.restart_warning", warning);
        }
        else
        {
            Styx.Ui.SetVarAll("styx.srm.restart_warning", 0);
        }

        // Wipe countdown — independent of Enabled (admins may want to keep
        // showing the wipe countdown even when restarts are paused).
        var nextWipe = ComputeNextWipe(now);
        if (nextWipe != DateTime.MaxValue)
        {
            var wrem = nextWipe - now;
            int wd = Math.Max(0, (int)wrem.TotalDays);
            int wh = Math.Max(0, wrem.Hours);
            Styx.Ui.SetVarAll("styx.srm.wipe_d", wd);
            Styx.Ui.SetVarAll("styx.srm.wipe_h", wh);
        }
        else
        {
            Styx.Ui.SetVarAll("styx.srm.wipe_d", 0);
            Styx.Ui.SetVarAll("styx.srm.wipe_h", 0);
        }

        // UI-side cvars consumed by the styxSrm admin window.
        Styx.Ui.SetVarAll("styx.srm.enabled", _cfg.Enabled ? 1 : 0);
        Styx.Ui.SetVarAll("styx.srm.cycle", _cfg.WipeCycleDays);

        // Days since last wipe (for the "Last wipe: Nd ago" display).
        if (_cfg.LastWipeUnix > 0)
        {
            long diff = UnixNow() - _cfg.LastWipeUnix;
            int daysSince = (int)Math.Max(0, diff / 86400);
            Styx.Ui.SetVarAll("styx.srm.last_wipe_d", daysSince);
        }
    }

    // ---- Admin UI ----

    private void OpenFor(EntityPlayer p)
    {
        if (p == null) return;
        var pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid)) return;
        if (!StyxCore.Perms.HasPermission(pid, Perm))
        {
            Styx.Server.Whisper(p, "[ff6666][SRM] You lack permission '" + Perm + "'.[-]");
            return;
        }
        _open.Add(p.entityId);
        _confirmPending.Remove(p.entityId);

        // Push current state so the panel renders correctly on first frame.
        Styx.Ui.SetVar(p, "styx.srm.open", 1f);
        Styx.Ui.SetVar(p, "styx.srm.sel", 0f);
        Styx.Ui.SetVar(p, "styx.srm.confirm_pending", 0f);
        Styx.Ui.SetVar(p, "styx.srm.enabled", _cfg.Enabled ? 1f : 0f);
        Styx.Ui.SetVar(p, "styx.srm.cycle", _cfg.WipeCycleDays);

        Styx.Ui.Input.Acquire(p, Name);
        WhisperRow(p, 0);
    }

    private void CloseFor(EntityPlayer p)
    {
        if (p == null) return;
        Styx.Ui.SetVar(p, "styx.srm.open", 0f);
        Styx.Ui.Input.Release(p, Name);
        _open.Remove(p.entityId);
        _confirmPending.Remove(p.entityId);
    }

    void OnPlayerInput(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (p?.Buffs == null || !_open.Contains(p.entityId)) return;
        if ((int)p.Buffs.GetCustomVar("styx.srm.open") != 1) return;

        int sel = (int)p.Buffs.GetCustomVar("styx.srm.sel");

        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
                sel = (sel + 1) % UiRowCount;
                Styx.Ui.SetVar(p, "styx.srm.sel", sel);
                ClearPendingConfirm(p);
                WhisperRow(p, sel);
                break;
            case Styx.Ui.StyxInputKind.Crouch:
                sel = (sel - 1 + UiRowCount) % UiRowCount;
                Styx.Ui.SetVar(p, "styx.srm.sel", sel);
                ClearPendingConfirm(p);
                WhisperRow(p, sel);
                break;
            case Styx.Ui.StyxInputKind.PrimaryAction:
                if      (sel == 0) ToggleEnabled(p);
                else if (sel == 1) CycleWipeDays(p);
                else if (sel == 2) ResetLastWipe(p);
                else if (sel == 3) ConfirmRestartNow(p);
                break;
            case Styx.Ui.StyxInputKind.SecondaryAction:
                CloseFor(p);
                Styx.Scheduling.Scheduler.Once(0.05,
                    () => Styx.Ui.Menu.OpenLauncher?.Invoke(p),
                    name: "SRM.BackToLauncher");
                break;
        }
    }

    private void ToggleEnabled(EntityPlayer p)
    {
        _cfg.Enabled = !_cfg.Enabled;
        StyxCore.Configs.Save(this, _cfg);
        Styx.Ui.SetVar(p, "styx.srm.enabled", _cfg.Enabled ? 1f : 0f);
        Styx.Server.Whisper(p, _cfg.Enabled
            ? "[00ff66][SRM] Restart scheduling ENABLED.[-]"
            : "[ffaa00][SRM] Restart scheduling DISABLED. (Wipe countdown still updates.)[-]");
        Log.Out("[ServerRestartManager] {0} toggled Enabled -> {1}",
            StyxCore.Player.DisplayName(p), _cfg.Enabled);
    }

    private void CycleWipeDays(EntityPlayer p)
    {
        // Find current value's index in presets, advance to next (wrap).
        int currentIdx = -1;
        for (int i = 0; i < WipeCyclePresets.Length; i++)
            if (WipeCyclePresets[i] == _cfg.WipeCycleDays) { currentIdx = i; break; }
        int nextIdx = (currentIdx + 1) % WipeCyclePresets.Length;
        _cfg.WipeCycleDays = WipeCyclePresets[nextIdx];
        StyxCore.Configs.Save(this, _cfg);

        Styx.Ui.SetVar(p, "styx.srm.cycle", _cfg.WipeCycleDays);
        // Also push fresh wipe countdown so the HUD updates instantly.
        var nextWipe = ComputeNextWipe(DateTime.Now);
        if (nextWipe != DateTime.MaxValue)
        {
            var wrem = nextWipe - DateTime.Now;
            Styx.Ui.SetVarAll("styx.srm.wipe_d", Math.Max(0, (int)wrem.TotalDays));
            Styx.Ui.SetVarAll("styx.srm.wipe_h", Math.Max(0, wrem.Hours));
        }

        Styx.Server.Whisper(p, string.Format(
            "[00ff66][SRM] Wipe cycle -> {0} days. Next wipe: {1:yyyy-MM-dd HH:mm}[-]",
            _cfg.WipeCycleDays, nextWipe));
    }

    private void ResetLastWipe(EntityPlayer p)
    {
        _cfg.LastWipeUnix = UnixNow();
        StyxCore.Configs.Save(this, _cfg);
        // Force-refresh the wipe countdown immediately.
        var nextWipe = ComputeNextWipe(DateTime.Now);
        Styx.Ui.SetVar(p, "styx.srm.last_wipe_d", 0);

        Styx.Server.Whisper(p, string.Format(
            "[00ff66][SRM] Last-wipe timestamp reset to NOW. Next wipe: {0:yyyy-MM-dd HH:mm}[-]",
            nextWipe));
        Log.Out("[ServerRestartManager] {0} reset LastWipeUnix to {1}",
            StyxCore.Player.DisplayName(p), _cfg.LastWipeUnix);
    }

    private void ConfirmRestartNow(EntityPlayer p)
    {
        double now = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        if (_confirmPending.TryGetValue(p.entityId, out var firstTap)
            && (now - firstTap) <= ConfirmWindowSec)
        {
            // Second tap within window — execute.
            _confirmPending.Remove(p.entityId);
            Styx.Ui.SetVar(p, "styx.srm.confirm_pending", 0f);
            Styx.Server.Whisper(p, "[ff3030][SRM] Confirmed — triggering restart NOW...[-]");
            Log.Out("[ServerRestartManager] {0} triggered immediate restart via UI",
                StyxCore.Player.DisplayName(p));
            CloseFor(p);
            TriggerRestart();
        }
        else
        {
            // First tap — arm confirm window.
            _confirmPending[p.entityId] = now;
            Styx.Ui.SetVar(p, "styx.srm.confirm_pending", 1f);
            Styx.Server.Whisper(p, string.Format(
                "[ffaa00][SRM] Press LMB again within {0}s to confirm immediate restart.[-]",
                (int)ConfirmWindowSec));
        }
    }

    private void ClearPendingConfirm(EntityPlayer p)
    {
        if (_confirmPending.Remove(p.entityId))
            Styx.Ui.SetVar(p, "styx.srm.confirm_pending", 0f);
    }

    private void WhisperRow(EntityPlayer p, int row)
    {
        var nextWipe = ComputeNextWipe(DateTime.Now);
        switch (row)
        {
            case 0:
                Styx.Server.Whisper(p, string.Format(
                    "[ccddff][SRM] Status:[-] [ffffdd]{0}[-] (LMB toggles)",
                    _cfg.Enabled ? "[00ff66]ENABLED[-]" : "[ffaa00]DISABLED[-]"));
                break;
            case 1:
                Styx.Server.Whisper(p, string.Format(
                    "[ccddff][SRM] Wipe cycle:[-] [ffffdd]{0} days[-] (LMB cycles preset). " +
                    "Next wipe: [ffffdd]{1:yyyy-MM-dd HH:mm}[-]",
                    _cfg.WipeCycleDays,
                    nextWipe == DateTime.MaxValue ? (object)"—" : nextWipe));
                break;
            case 2:
                long diff = UnixNow() - _cfg.LastWipeUnix;
                int daysSince = (int)Math.Max(0, diff / 86400);
                int hoursSince = (int)Math.Max(0, (diff % 86400) / 3600);
                Styx.Server.Whisper(p, string.Format(
                    "[ccddff][SRM] Reset last-wipe:[-] last wipe was [ffffdd]{0}d {1}h ago[-]. " +
                    "LMB resets to NOW (use after performing a wipe).",
                    daysSince, hoursSince));
                break;
            case 3:
                Styx.Server.Whisper(p, string.Format(
                    "[ccddff][SRM] Trigger restart NOW:[-] [ff6666]two-tap LMB within {0}s to confirm[-]. " +
                    "Next scheduled restart: [ffffdd]{1:HH:mm}[-]",
                    (int)ConfirmWindowSec, _nextRestart));
                break;
        }
    }

    private void SanitizeConfig()
    {
        if (_cfg == null) _cfg = new Configuration();
        if (_cfg.RestartTimes == null) _cfg.RestartTimes = new List<RestartTime>();

        // Snapshot serialized form pre-sanitize. We only write back to disk
        // if sanitize actually materially changed something — otherwise a
        // user-saved ordering (e.g. 22:00, 6:00, 14:00) gets reordered, we
        // save, the watcher fires, we reload and sanitize again: infinite
        // loop. Diff check breaks the loop.
        string before = JsonConvert.SerializeObject(_cfg);

        _cfg.RestartTimes = _cfg.RestartTimes
            .Where(t => t != null && t.Hour >= 0 && t.Hour < 24 && t.Minute >= 0 && t.Minute < 60)
            .GroupBy(t => new { t.Hour, t.Minute })
            .Select(g => g.First())
            .OrderBy(t => t.Hour).ThenBy(t => t.Minute)
            .ToList();

        if (_cfg.WarningStartMinutes < 1) _cfg.WarningStartMinutes = 20;
        if (_cfg.FinalCountdownStart < 0) _cfg.FinalCountdownStart = 30;
        if (_cfg.FinalCountdownEvery < 1) _cfg.FinalCountdownEvery = 5;

        string after = JsonConvert.SerializeObject(_cfg);
        if (before != after) StyxCore.Configs.Save(this, _cfg);
    }

    // ---- Commands ----

    private bool RequireAdmin(Styx.Commands.CommandContext ctx)
    {
        string id = ctx.Client?.PlatformId?.CombinedString;
        if (!string.IsNullOrEmpty(id) && StyxCore.Perms.HasPermission(id, Perm)) return true;
        if (ctx.Client == null) return true; // console
        ctx.Reply("You don't have permission for /srm.");
        return false;
    }

    private void ShowStatus(Styx.Commands.CommandContext ctx)
    {
        var remaining = _nextRestart - DateTime.Now;
        ctx.Reply($"Enabled: {_cfg.Enabled}");
        ctx.Reply($"Next restart: {_nextRestart:yyyy-MM-dd HH:mm} (in {FormatTimespan(remaining)})");
        ctx.Reply($"Warning start: {_cfg.WarningStartMinutes} min   Final countdown: {_cfg.FinalCountdownStart}s");
    }

    private void ShowTimes(Styx.Commands.CommandContext ctx)
    {
        if (_cfg.RestartTimes.Count == 0) { ctx.Reply("No restart times configured."); return; }
        ctx.Reply("Configured restart times:");
        foreach (var t in _cfg.RestartTimes) ctx.Reply("  " + t);
    }

    private static string FormatTimespan(TimeSpan ts)
    {
        if (ts.TotalSeconds < 0) return "passed";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }
}
