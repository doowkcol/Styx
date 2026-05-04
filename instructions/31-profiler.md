# 31 — StyxProfiler

Chat-rendered performance dashboard for the framework. The framework instruments every hook fire (with per-plugin attribution), every chat command, every scheduler timer, and every Harmony patch. This plugin reads those snapshots and prints them.

Lock-free recording — counters are `Interlocked.Add` and `ConcurrentDictionary` writes only. **Zero overhead when disabled** (`/prof off` flips a single `volatile bool`; record paths short-circuit).

## Commands

All admin-only — gated by `styx.prof.use`.

| Command | What |
|---|---|
| `/prof` | Summary — uptime, total hook fires, total command dispatches, top hooks, top plugins, GC headline |
| `/prof hooks` | Top 12 hooks by total handler time (cumulative across all plugins) |
| `/prof plugins` | Per-plugin handler totals across all hooks (which plugin spends the most time in handlers) |
| `/prof commands` | Top 12 chat commands by total dispatch time |
| `/prof timers` | Top 12 scheduler timers by total time |
| `/prof patches` | Top 12 Harmony patches by total time (only patches that explicitly call `Profiler.RecordPatch` — first-party direct-work patches) |
| `/prof gc` | Heap size + Gen 0 / 1 / 2 collection counts and deltas since last `reset` |
| `/prof reset` | Zero all counters and rebaseline the elapsed clock |
| `/prof on` / `/prof off` | Toggle recording entirely |

## Permissions

| Perm | What |
|---|---|
| `styx.prof.use` | Run any `/prof` subcommand |

## What's instrumented

| Layer | Tracked | Source |
|---|---|---|
| **Hook fires** | Call count, total/max ticks per hook, per-plugin attribution per hook (calls + total ticks + errors) | `Styx.HookManager.Fire` |
| **Chat commands** | Call count, total/max ticks per command name | `Styx.Commands.CommandManager.TryDispatch` |
| **Scheduler timers** | Call count, total/max ticks per timer name. Plugin name extracted from `<plugin>.<timer>` naming convention. | `Styx.Scheduling.Scheduler.Pump` |
| **Harmony patches** | Call count, total ticks per patch. Only patches that explicitly call `Styx.Profiler.RecordPatch` show up — first-party patches in `Styx.Hooks.FirstParty.*` are wired up; third-party Harmony patches are invisible by design. | Manual instrumentation in patch bodies |
| **GC** | `GC.GetTotalMemory(false)`, `GC.CollectionCount(0/1/2)` sampled on every `GetSnapshot`. Deltas computed against the last `reset` baseline. | `Styx.Profiler.GetSnapshot` |

Hook handler attribution is the headline feature — `/prof plugins` tells you exactly which plugin is dominating handler time, which is the right granularity for "is plugin X expensive?" questions.

## Reading the output

### `/prof`

```
[StyxProfiler] uptime 12m34s · hooks 18432 fires · cmds 27 dispatches
top hooks: OnUpdateLive 14.83ms · OnPlayerInput 13.84ms · OnEntitySpawned 2.41ms
top plugins: StyxTeleport 13.84ms · StyxShield 12.79ms · StyxBuilder 1.20ms
gc: heap 132 MB (+8 MB) · gen0 14 (+3) · gen1 2 (+1) · gen2 0 (=)
```

- Hook total time = sum across all plugins' handlers for that hook. A hook with no handlers fires for free.
- Plugin total time = sum across every hook this plugin handles. Plugins that don't subscribe to anything don't appear.
- GC numbers are `(absolute)` plus `(delta since last /prof reset)` in parens.

### `/prof hooks`

Each row: `OnX  N fires · totalMs · maxMs/fire · errors`

`totalMs / N` gives average per-fire cost. `maxMs/fire` is the worst single fire — useful for spotting allocation spikes.

### `/prof patches`

Only first-party patches show up by default — `Styx.Hooks.FirstParty.ShieldGuard.SetAttackTarget`, `Styx.Hooks.FirstParty.ShieldGuard.OnUpdateLive`, etc. Third-party plugin-author patches need to opt in by calling `Styx.Profiler.RecordPatch(name, ticks, success)` from their Prefix/Postfix.

## Operator notes

- `/prof off` is genuinely free overhead — leave it on by default and only flip off if benchmarking something else on the box.
- `/prof reset` is the right move when investigating a regression — run it, repro the slow operation, run `/prof` and read deltas.
- Hook total time of `~14ms over 12 minutes` is **fine**. The tail entries (`Active Xs`-style cooldowns, restart timers) all read sub-microsecond per fire.
- A single hook eating > 50 ms cumulative or > 1 ms max-per-fire is worth looking at. Check `/prof plugins` to find which plugin is dominating that hook.
