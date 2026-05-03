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

// StyxProfiler v0.1.0 -- presentation layer for Styx.Profiler.
//
// The framework records call counts + cumulative tick timing for every
// hook fire (with per-plugin attribution) and every chat command. This
// plugin reads those snapshots and renders them.
//
// Commands (admin only -- styx.prof.use):
//   /prof              -- summary: uptime, totals, top hooks/plugins, GC
//   /prof hooks        -- top 12 hooks by total handler time
//   /prof plugins      -- per-plugin handler totals (across all hooks)
//   /prof commands     -- top 12 chat commands by total time
//   /prof timers       -- top 12 scheduler timers by total time
//   /prof patches      -- top 12 Harmony patches by total time
//   /prof gc           -- heap size + Gen0/1/2 collection counts + deltas
//   /prof reset        -- zero counters and rebaseline
//   /prof off / on     -- toggle recording entirely (zero overhead when off)
//
// Phase 1 instrumented HookManager.Fire and CommandManager.TryDispatch.
// Phase 2 adds Scheduler.Pump per-timer attribution + GC delta tracking.
// Phase 3 adds Harmony patch instrumentation. Patches that dispatch via
// HookManager.Fire are already covered at the hook layer; direct-work
// patches (like Styx.Hooks.FirstParty.ShieldGuard) call Profiler.RecordPatch
// from their Prefix/Postfix to surface per-tick cost.

using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Plugins;

[Info("StyxProfiler", "Doowkcol", "0.3.0")]
public class StyxProfiler : StyxPlugin
{
    public override string Description => "Live profiler for Styx hooks + commands";

    private const string PermUse = "styx.prof.use";

    public override void OnLoad()
    {
        StyxCore.Perms.RegisterKnown(PermUse,
            "Use /prof to inspect framework profiling stats", Name);

        StyxCore.Commands.Register("prof",
            "Show framework profiling stats -- /prof [hooks|plugins|commands|timers|patches|gc|reset|on|off]",
            CmdProf);

        Log.Out("[StyxProfiler] Loaded v0.3.0 -- profiler enabled={0}, ticks/sec={1}",
            Profiler.Enabled, Profiler.TicksPerSecond);
    }

    public override void OnUnload()
    {
        StyxCore.Perms.UnregisterKnownByOwner(Name);
    }

    private void CmdProf(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (ctx.Client == null) { ctx.Reply("Run from in-game chat or admin console."); return; }
        var pid = ctx.Client.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid)) { ctx.Reply("[ff6666]Could not resolve player id.[-]"); return; }
        if (!StyxCore.Perms.HasPermission(pid, PermUse))
        { ctx.Reply("[ff6666]No permission '" + PermUse + "'.[-]"); return; }

        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "summary";
        switch (sub)
        {
            case "hooks":    DumpHooks(ctx);    return;
            case "plugins":  DumpPlugins(ctx);  return;
            case "commands": DumpCommands(ctx); return;
            case "timers":   DumpTimers(ctx);   return;
            case "patches":  DumpPatches(ctx);  return;
            case "gc":       DumpGc(ctx);       return;
            case "reset":
                Profiler.Reset();
                ctx.Reply("[00ff66][Profiler] Counters reset and clock rebaselined.[-]");
                return;
            case "on":
                Profiler.Enabled = true;
                ctx.Reply("[00ff66][Profiler] Recording enabled.[-]");
                return;
            case "off":
                Profiler.Enabled = false;
                ctx.Reply("[ffaa00][Profiler] Recording disabled (zero overhead, counters frozen).[-]");
                return;
            case "summary":
            default:
                Summary(ctx); return;
        }
    }

    // ============================================================ Renderers

    private void Summary(Styx.Commands.CommandContext ctx)
    {
        var snap = Profiler.GetSnapshot();
        long totalHookCalls = 0, totalHookTicks = 0;
        foreach (var h in snap.Hooks) { totalHookCalls += h.Calls; totalHookTicks += h.TotalTicks; }
        long totalCmdCalls = 0, totalCmdTicks = 0;
        foreach (var c in snap.Commands) { totalCmdCalls += c.Calls; totalCmdTicks += c.TotalTicks; }

        double totalHookMs = TicksToMs(totalHookTicks);
        double totalCmdMs  = TicksToMs(totalCmdTicks);
        double cpuPct = totalHookMs / 1000.0 / Math.Max(0.001, snap.ElapsedSeconds) * 100.0;

        long totalTimerCalls = 0, totalTimerTicks = 0;
        foreach (var t in snap.Timers) { totalTimerCalls += t.Calls; totalTimerTicks += t.TotalTicks; }
        double totalTimerMs = TicksToMs(totalTimerTicks);

        ctx.Reply(string.Format("[ccddff][Profiler] up {0:F1}s, recording={1}.[-]",
            snap.ElapsedSeconds, Profiler.Enabled));
        ctx.Reply(string.Format("[ccddff]Hooks: {0} tracked, {1:N0} calls, {2:F1}ms total ({3:F2}% CPU).[-]",
            snap.Hooks.Count, totalHookCalls, totalHookMs, cpuPct));
        ctx.Reply(string.Format("[ccddff]Commands: {0} tracked, {1:N0} calls, {2:F1}ms total.[-]",
            snap.Commands.Count, totalCmdCalls, totalCmdMs));
        ctx.Reply(string.Format("[ccddff]Timers: {0} tracked, {1:N0} fires, {2:F1}ms total.[-]",
            snap.Timers.Count, totalTimerCalls, totalTimerMs));

        long totalPatchCalls = 0, totalPatchTicks = 0;
        foreach (var p in snap.Patches) { totalPatchCalls += p.Calls; totalPatchTicks += p.TotalTicks; }
        double totalPatchMs = TicksToMs(totalPatchTicks);
        ctx.Reply(string.Format("[ccddff]Patches: {0} tracked, {1:N0} fires, {2:F1}ms total.[-]",
            snap.Patches.Count, totalPatchCalls, totalPatchMs));
        ctx.Reply(string.Format("[ccddff]Heap: {0} ({1}{2:N0} since reset)   GC: {3}/{4}/{5} (Δ {6}/{7}/{8})[-]",
            FormatBytes(snap.Gc.HeapBytes),
            snap.Gc.HeapDelta >= 0 ? "+" : "",
            snap.Gc.HeapDelta,
            snap.Gc.Gen0, snap.Gc.Gen1, snap.Gc.Gen2,
            snap.Gc.Gen0Delta, snap.Gc.Gen1Delta, snap.Gc.Gen2Delta));

        // Top 5 hooks by total time
        var topHooks = snap.Hooks.OrderByDescending(h => h.TotalTicks).Take(5).ToList();
        if (topHooks.Count > 0)
        {
            ctx.Reply("[ffffdd]Top 5 hooks by total handler time:[-]");
            foreach (var h in topHooks)
                ctx.Reply(string.Format("  [-] [ffffff]{0}[-]  {1:N0} calls   {2}   max {3}",
                    h.Name, h.Calls, FormatMs(h.TotalMs), FormatMs(h.MaxMs)));
        }
    }

    private void DumpHooks(Styx.Commands.CommandContext ctx)
    {
        var snap = Profiler.GetSnapshot();
        var sorted = snap.Hooks.OrderByDescending(h => h.TotalTicks).Take(12).ToList();
        ctx.Reply(string.Format("[ccddff][Profiler] Top {0} hooks (of {1}) by total handler time over {2:F1}s:[-]",
            sorted.Count, snap.Hooks.Count, snap.ElapsedSeconds));
        foreach (var h in sorted)
        {
            double rate = h.Calls / Math.Max(0.001, snap.ElapsedSeconds);
            ctx.Reply(string.Format("  [-] [ffffff]{0}[-]  {1:N0} calls ({2:F1}/s)   total {3}   max {4}",
                h.Name, h.Calls, rate, FormatMs(h.TotalMs), FormatMs(h.MaxMs)));
        }
    }

    private void DumpPlugins(Styx.Commands.CommandContext ctx)
    {
        var snap = Profiler.GetSnapshot();

        // Aggregate per plugin across every hook.
        var byPlugin = new Dictionary<string, (long calls, long ticks, long max, long errors, int hookCount)>(
            StringComparer.Ordinal);
        foreach (var h in snap.Hooks)
        {
            foreach (var p in h.PerPlugin)
            {
                byPlugin.TryGetValue(p.Plugin, out var agg);
                agg.calls   += p.Calls;
                agg.ticks   += p.TotalTicks;
                agg.max      = Math.Max(agg.max, p.MaxTicks);
                agg.errors  += p.Errors;
                agg.hookCount++;
                byPlugin[p.Plugin] = agg;
            }
        }

        var sorted = byPlugin.OrderByDescending(kv => kv.Value.ticks).Take(12).ToList();
        ctx.Reply(string.Format("[ccddff][Profiler] Top {0} plugins by handler time over {1:F1}s:[-]",
            sorted.Count, snap.ElapsedSeconds));
        foreach (var kv in sorted)
        {
            var v = kv.Value;
            string errs = v.errors > 0 ? string.Format(" [ff6666]{0} errors[-]", v.errors) : "";
            ctx.Reply(string.Format("  [-] [ffffff]{0}[-]  hooks={1}   {2:N0} calls   total {3}   max {4}{5}",
                kv.Key, v.hookCount, v.calls,
                FormatMs(TicksToMs(v.ticks)), FormatMs(TicksToMs(v.max)), errs));
        }
    }

    private void DumpCommands(Styx.Commands.CommandContext ctx)
    {
        var snap = Profiler.GetSnapshot();
        var sorted = snap.Commands.OrderByDescending(c => c.TotalTicks).Take(12).ToList();
        ctx.Reply(string.Format("[ccddff][Profiler] Top {0} commands (of {1}) by total time over {2:F1}s:[-]",
            sorted.Count, snap.Commands.Count, snap.ElapsedSeconds));
        foreach (var c in sorted)
        {
            string errs = c.Errors > 0 ? string.Format(" [ff6666]{0} errors[-]", c.Errors) : "";
            ctx.Reply(string.Format("  [-] [ffffff]/{0}[-] [{1}]  {2} calls   total {3}   max {4}{5}",
                c.Name, c.Plugin, c.Calls, FormatMs(c.TotalMs), FormatMs(c.MaxMs), errs));
        }
    }

    private void DumpTimers(Styx.Commands.CommandContext ctx)
    {
        var snap = Profiler.GetSnapshot();
        var sorted = snap.Timers.OrderByDescending(t => t.TotalTicks).Take(12).ToList();
        ctx.Reply(string.Format("[ccddff][Profiler] Top {0} timers (of {1}) by total time over {2:F1}s:[-]",
            sorted.Count, snap.Timers.Count, snap.ElapsedSeconds));
        if (sorted.Count == 0)
        {
            ctx.Reply("[ffaa00]  (no scheduler timers fired since baseline)[-]");
            return;
        }
        foreach (var t in sorted)
        {
            double rate = t.Calls / Math.Max(0.001, snap.ElapsedSeconds);
            string errs = t.Errors > 0 ? string.Format(" [ff6666]{0} errors[-]", t.Errors) : "";
            ctx.Reply(string.Format("  [-] [ffffff]{0}[-] [{1}]  {2:N0} calls ({3:F2}/s)   total {4}   max {5}{6}",
                t.Name, t.Plugin, t.Calls, rate, FormatMs(t.TotalMs), FormatMs(t.MaxMs), errs));
        }
    }

    private void DumpPatches(Styx.Commands.CommandContext ctx)
    {
        var snap = Profiler.GetSnapshot();
        var sorted = snap.Patches.OrderByDescending(p => p.TotalTicks).Take(12).ToList();
        ctx.Reply(string.Format("[ccddff][Profiler] Top {0} patches (of {1}) by total time over {2:F1}s:[-]",
            sorted.Count, snap.Patches.Count, snap.ElapsedSeconds));
        if (sorted.Count == 0)
        {
            ctx.Reply("[ffaa00]  (no instrumented patches have fired yet)[-]");
            return;
        }
        foreach (var p in sorted)
        {
            double rate = p.Calls / Math.Max(0.001, snap.ElapsedSeconds);
            double avgUs = p.Calls > 0 ? (p.TotalTicks * 1_000_000.0 / Profiler.TicksPerSecond / p.Calls) : 0;
            string errs = p.Errors > 0 ? string.Format(" [ff6666]{0} errors[-]", p.Errors) : "";
            ctx.Reply(string.Format("  [-] [ffffff]{0}[-]  {1:N0} calls ({2:F1}/s)   avg {3:F1}μs   total {4}   max {5}{6}",
                p.Name, p.Calls, rate, avgUs, FormatMs(p.TotalMs), FormatMs(p.MaxMs), errs));
        }
    }

    private void DumpGc(Styx.Commands.CommandContext ctx)
    {
        var snap = Profiler.GetSnapshot();
        var g = snap.Gc;
        ctx.Reply(string.Format("[ccddff][Profiler] GC over {0:F1}s since reset:[-]", snap.ElapsedSeconds));
        ctx.Reply(string.Format("  [-] Heap now: [ffffff]{0}[-]   delta since reset: [{2}]{1}[-]",
            FormatBytes(g.HeapBytes),
            FormatSignedBytes(g.HeapDelta),
            g.HeapDelta < 0 ? "00ff66" : g.HeapDelta < 50_000_000L ? "ffffdd" : "ffaa00"));
        ctx.Reply(string.Format("  [-] Collections (Gen0 / Gen1 / Gen2): [ffffff]{0} / {1} / {2}[-]   deltas: +{3} / +{4} / +{5}",
            g.Gen0, g.Gen1, g.Gen2, g.Gen0Delta, g.Gen1Delta, g.Gen2Delta));

        // Sanity / heuristic notes
        double sec = Math.Max(0.001, snap.ElapsedSeconds);
        double allocRate = Math.Max(0, g.HeapDelta) / sec;  // rough lower-bound
        double gen0Rate  = g.Gen0Delta / sec;
        if (g.Gen2Delta > 0)
            ctx.Reply(string.Format("[ffaa00]  Gen2 collections fired ({0}). Big-object/long-lived churn -- worth investigating.[-]",
                g.Gen2Delta));
        if (gen0Rate > 1.0)
            ctx.Reply(string.Format("[ffaa00]  Gen0 rate {0:F1}/s -- elevated allocation pressure.[-]", gen0Rate));
        if (allocRate > 1_000_000)
            ctx.Reply(string.Format("[ffaa00]  Net heap growth ~{0}/s -- check for leaks if sustained.[-]",
                FormatBytes((long)allocRate)));
    }

    // ============================================================ Helpers

    private static double TicksToMs(long ticks) =>
        ticks * 1000.0 / Profiler.TicksPerSecond;

    /// <summary>Pretty-print ms with sensible units. &lt;1ms shows µs; ≥1s shows seconds.</summary>
    private static string FormatMs(double ms)
    {
        if (ms < 1.0)        return string.Format("{0:F0}μs", ms * 1000.0);
        if (ms < 1000.0)     return string.Format("{0:F2}ms", ms);
        return string.Format("{0:F2}s", ms / 1000.0);
    }

    /// <summary>Pretty-print byte counts. Always positive.</summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024)         return string.Format("{0} B", bytes);
        if (bytes < 1024 * 1024)  return string.Format("{0:F1} KB", bytes / 1024.0);
        if (bytes < 1024 * 1024 * 1024) return string.Format("{0:F1} MB", bytes / (1024.0 * 1024.0));
        return string.Format("{0:F2} GB", bytes / (1024.0 * 1024.0 * 1024.0));
    }

    /// <summary>Like FormatBytes but with explicit sign for deltas.</summary>
    private static string FormatSignedBytes(long bytes)
    {
        string sign = bytes >= 0 ? "+" : "-";
        return sign + FormatBytes(Math.Abs(bytes));
    }
}
