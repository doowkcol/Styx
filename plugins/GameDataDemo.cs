// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// Demo plugin for Styx v0.6.0 GameData runtime-mutation API.
//
// Exercises Items / Recipes / Buffs read + modify. Every mutation is recorded
// by the framework and automatically reverted on plugin unload (hot-reload
// safe) — see GameDataManager in Styx.Core.
//
// Try it: /gd probe <itemName> reads a few fields; /gd fast halves
// forge recipe craft times; /gd undo triggers a reload so the mutations roll
// back. Verify with probe before/after.

using Styx;
using Styx.Plugins;

[Info("GameDataDemo", "Doowkcol", "0.1.0")]
public class GameDataDemo : StyxPlugin
{
    public override string Description => "Styx v0.6.0 GameData API demo";

    public override void OnLoad()
    {
        StyxCore.Commands.Register("gd", "GameData API demo — subcommands: probe, fast, slow", (ctx, args) =>
        {
            if (args.Length == 0) { Help(ctx); return; }

            switch (args[0].ToLowerInvariant())
            {
                case "probe":
                    Probe(ctx, args);
                    break;

                case "fast":
                    FastCraft(ctx);
                    break;

                case "slow":
                    SlowCraft(ctx);
                    break;

                case "buff":
                    ProbeBuff(ctx, args);
                    break;

                default:
                    Help(ctx);
                    break;
            }
        });
    }

    private void Help(Styx.Commands.CommandContext ctx)
    {
        ctx.Reply("GameData demo commands:");
        ctx.Reply("  /gd probe <item>     — read a few ItemClass fields");
        ctx.Reply("  /gd fast             — halve craftingTime on meleeToolRepairT0StoneAxe recipe");
        ctx.Reply("  /gd slow             — 10x craftingTime on meleeToolRepairT0StoneAxe recipe");
        ctx.Reply("  /gd buff <name>      — read a few BuffClass fields");
        ctx.Reply("All mutations revert when this plugin unloads or reloads.");
    }

    private void Probe(Styx.Commands.CommandContext ctx, string[] args)
    {
        string name = args.Length > 1 ? args[1] : "meleeToolRepairT0StoneAxe";
        var item = GameData.Items.Get(name);
        if (item == null) { ctx.Reply("Item not found: " + name); return; }

        ctx.Reply(string.Format("Item '{0}':", name));
        ctx.Reply(string.Format("  Smell = {0}", item.Smell));
        ctx.Reply(string.Format("  IsSticky = {0}", item.IsSticky));
        ctx.Reply(string.Format("  HoldType.Value = {0}", item.HoldType?.Value));
        ctx.Reply(string.Format("  Stacknumber.Value = {0}", item.Stacknumber?.Value));
    }

    private void FastCraft(Styx.Commands.CommandContext ctx)
    {
        bool ok = GameData.Recipes.Modify("meleeToolRepairT0StoneAxe", r =>
        {
            float before = r.craftingTime;
            r.craftingTime = before * 0.5f;
            Log.Out("[GameDataDemo] meleeToolRepairT0StoneAxe craftingTime: {0} -> {1}", before, r.craftingTime);
        });
        ctx.Reply(ok ? "Crafting halved — reload plugin to revert." : "Recipe not found.");
    }

    private void SlowCraft(Styx.Commands.CommandContext ctx)
    {
        bool ok = GameData.Recipes.Modify("meleeToolRepairT0StoneAxe", r =>
        {
            float before = r.craftingTime;
            r.craftingTime = before * 10f;
            Log.Out("[GameDataDemo] meleeToolRepairT0StoneAxe craftingTime: {0} -> {1}", before, r.craftingTime);
        });
        ctx.Reply(ok ? "Crafting 10x'd — reload plugin to revert." : "Recipe not found.");
    }

    private void ProbeBuff(Styx.Commands.CommandContext ctx, string[] args)
    {
        string name = args.Length > 1 ? args[1] : "buffInfectionMain";
        var b = GameData.Buffs.Get(name);
        if (b == null) { ctx.Reply("Buff not found: " + name); return; }
        ctx.Reply(string.Format("Buff '{0}':", name));
        ctx.Reply(string.Format("  Name = {0}", b.Name));
        ctx.Reply(string.Format("  LocalizedName = {0}", b.LocalizedName));
        ctx.Reply(string.Format("  durationMax = {0}", b.durationMax));
    }
}
