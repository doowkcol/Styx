# 09 — ServerRestartManager

Scheduled daily server restarts with multi-stage warning broadcasts. Cleanly invokes the `shutdown` console command — your hosting wrapper / systemd / launcher is expected to relaunch.

## Commands

| Command | Perm | What |
|---|---|---|
| `/srm` | `styx.restart.admin` | Show next restart time + status |
| `/srm status` | `styx.restart.admin` | Same as `/srm` |
| `/srm times` | `styx.restart.admin` | Show all configured restart times |
| `/srm reload` | `styx.restart.admin` | Reload config (also auto-hot-reloads on save) |

## Permissions

| Perm | What |
|---|---|
| `styx.restart.admin` | Run `/srm` admin commands |

## Config — `configs/ServerRestartManager.json`

```json
{
  "RestartTimes": [ "06:00", "18:00" ],     // 24h format, server local time
  "WarningMinutes": [ 20, 15, 10, 5, 1 ],    // T-N minute warnings
  "FinalCountdownSeconds": 30,               // Per-second countdown for last 30s
  "WarningMessage": "[ff3030]Server restart in [ffffff]{minutes}[ff3030] minute(s).[-]",
  "CountdownMessage": "[ff3030]Restarting in [ffffff]{seconds}[ff3030]...[-]",
  "ShutdownCommand": "shutdown"
}
```

| Field | What |
|---|---|
| `RestartTimes` | Array of `HH:MM` strings (24h, server local time). Multiple per day OK. |
| `WarningMinutes` | Times before restart to broadcast warning. Default `[20, 15, 10, 5, 1]`. |
| `FinalCountdownSeconds` | Per-second countdown duration for the last N seconds. |
| `WarningMessage` / `CountdownMessage` | Templates with `{minutes}` / `{seconds}` substitution. BBCode colours OK. |
| `ShutdownCommand` | Console command to execute. Default `shutdown` is the engine's clean-exit. |

## Mechanics

1. On load, schedules every `RestartTimes` via `Styx.Scheduling.Scheduler`.
2. At each scheduled time, fires the warning sequence:
   - At T-20m, T-15m, T-10m, T-5m, T-1m → `WarningMessage` broadcast
   - For the final 30 seconds → per-second `CountdownMessage`
3. At T-0 → executes `ShutdownCommand` (default `shutdown`)
4. Engine performs clean save + close. Your hosting wrapper relaunches.

Schedule recomputes after each restart cycle — next-day occurrences fire automatically.

## Notes

- **No restart looper built in** — Styx tells the engine to shut down cleanly. Your launcher (PowerShell while-loop, systemd unit, Pterodactyl auto-restart, etc.) handles the relaunch.
- **All times are server-local** — match your `serverconfig.xml` time zone, not players' local time.
- **Multiple times per day**: just add more entries to `RestartTimes`. Common pattern: `["06:00", "18:00"]` for 12-hour cycles.
- **Hot-reload** on config save — schedule rebuilds without restart.
- **Pair with WelcomeMessage**: post-restart welcome reminds players the server just restarted (no reconnect lag = expected).

## Common ops

### Change to a single daily restart

```json
"RestartTimes": [ "04:00" ]
```

### Add a "soft" extra restart with no countdown spam

Use a separate `WarningMinutes` of just `[5]` for emergency-restart cases. Edit, save, hot-reloads.

### Abort an in-progress countdown

Currently no kill switch — restart cycle proceeds once started. Workaround: `srm reload` doesn't cancel a running countdown. To bail, edit the config to remove the imminent time + restart Styx.Core (heavy-handed).

### Hosting integrations

- **Windows**: wrap `startdedicated.bat` in a `:loop\nstartdedicated.bat\ngoto loop` script
- **Linux/systemd**: use `Restart=always` in your service unit
- **Pterodactyl**: auto-restart on clean exit is the default
