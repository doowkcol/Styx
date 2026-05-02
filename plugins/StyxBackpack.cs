// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// StyxBackpack — per-player persistent storage container opened from the
// launcher / chat command. Contents survive log-out and (optionally) death.
//
// ARCHITECTURE (see docs/styx_xui_recipe notes for the engine-mechanics
// reasoning; several 7DTD-specific gotchas in here):
//
// The container is NOT a permanent world block. On each /b invoke
// we spawn a short-lived EntityBackpack at the player's feet, hydrate its
// TileEntityLootContainer from a per-player JSON save file, and trigger
// the engine's native "open loot UI" path via GameManager.TELockServer.
// The loot window opens — and the vanilla XUi flow co-opens the player's
// backpack inventory panel alongside it (see XUiC_LootWindowGroup.cs:70),
// so the player sees both panels at once for normal drag-drop.
//
// When the player closes the UI, client auto-sends UnlockServer. We also
// poll TileEntityLootContainer.IsUserAccessing at 2Hz and when it flips
// true → false we:
//   1. Serialize every ItemStack in the container to a binary blob
//      (via ItemStack.Write/Read) and base64-encode it into JSON.
//   2. Remove the transient EntityBackpack from the world.
//
// Periodic autosave (every 2s while open) keeps us crash-safe within a
// 2-second window.
//
// ON DEATH (OnEntityDeath → EntityPlayer):
//   - If player has KeepOnDeath perm: leave the data file untouched.
//   - Else: load saved contents, spawn a loot-bag (EntityBackpack with
//     bPlayerBackpack=false, RefPlayerId=-1) at the death position with
//     those items, schedule despawn after DropLifetimeSeconds, then clear
//     the data file.
//
// PERM TIERS (first-match-wins, à la StyxZombieRadar):
//   styx.backpack.master  → 48 slots (6 rows × 8 cols)
//   styx.backpack.vip     → 32 slots (4 × 8)
//   styx.backpack.use     → 24 slots (3 × 8)
//   no matching perm      → launcher entry hidden, /backpack refused
//   styx.backpack.keep_on_death → persistence through death (separate perm)
//
// SCOPE NOTE: a v0.2 attempt to make the stash visible to crafting + ammo
// reload (Harmony-patched Bag.GetItemCount/DecItem + /b take/store chat
// commands) was scrapped. The patches only fire server-side, but the
// reload + recipe UI gates check the bag CLIENT-side, so the auto-pull
// was invisible to the player. The chat-command workaround was usable
// but felt janky. Decision: keep it simple — backpack is just storage.
// Move things in/out via the loot UI normally.
//
// SUBCOMMANDS:
//   /b                — open backpack (default)
//   /b status         — tier, slot count, save state
//   /b clear          — wipe own save file (escape hatch from the spec)
//   /b drop           — test the death-drop pathway WITHOUT dying for real
//   /b sweep          — admin-only: scan world + despawn orphan stash bags
//                       (also runs once on server boot)

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Styx;
using Styx.Plugins;
using Styx.Scheduling;
using UnityEngine;

[Info("StyxBackpack", "Doowkcol", "0.3.0")]
public class StyxBackpack : StyxPlugin
{
    public override string Description => "Per-player persistent storage — launcher-opened, perm-tiered size, optional keep-on-death";

    // ============================================================ config

    public class SizeTier
    {
        public string Perm;
        public int Rows = 3;   // height (×8 cols = slot count)
    }

    public class Config
    {
        public bool Enabled = true;

        /// <summary>Grid width. 8 is the 7DTD standard (matches inventory).
        /// Other values render but are untested.</summary>
        public int Cols = 8;

        /// <summary>Tiered size — first perm the player has wins. No match =
        /// no backpack (launcher entry hidden, /backpack refused).</summary>
        public List<SizeTier> SizeByPerm = new List<SizeTier>
        {
            new SizeTier { Perm = "styx.backpack.master", Rows = 6 },
            new SizeTier { Perm = "styx.backpack.vip",    Rows = 4 },
            new SizeTier { Perm = "styx.backpack.use",    Rows = 3 },
        };

        /// <summary>Perm that, when held, keeps the backpack through death.
        /// Without this perm: contents drop as a loot bag + data file cleared.</summary>
        public string KeepOnDeathPerm = "styx.backpack.keep_on_death";

        /// <summary>Lifetime of the drop-bag spawned when a player dies
        /// without KeepOnDeath. Mirrors ZombieLoot's bag despawn pattern.</summary>
        public int DropLifetimeSeconds = 1800;  // 30 min

        /// <summary>Autosave cadence while a backpack is open. Lower = more
        /// crash-safe, higher = less I/O. 2s is a sensible default.</summary>
        public double AutosaveSeconds = 2.0;

        /// <summary>Polling cadence for close detection (IsUserAccessing
        /// edge). Must be ≥ autosave to actually catch the close.</summary>
        public double PollSeconds = 0.5;

        /// <summary>True = log every autosave with item count + bytes. Useful
        /// for debugging persistence; spammy in normal use.</summary>
        public bool VerboseAutosave = false;
    }

    // ============================================================ save-file schema

    /// <summary>On-disk format for per-player backpack state. `StacksB64`
    /// is base64 of <c>ItemStack[].Write(BinaryWriter)</c> so quality,
    /// mods and durability all round-trip cleanly.</summary>
    private class SaveFile
    {
        public int SchemaVersion { get; set; } = 1;
        public long SavedAtUnix  { get; set; }
        public int Rows          { get; set; }
        public int Cols          { get; set; }
        public string StacksB64  { get; set; } = "";
    }

    // ============================================================ runtime state

    /// <summary>Active backpack session — one per player with the UI open.</summary>
    private class Session
    {
        public int PlayerEntityId;
        public string Pid;
        public int BackpackEntityId;
        public TileEntityLootContainer Loot;
        public Vector3i EntityPos;
        public bool WasAccessing;       // last poll value — close = true→false
        public double LastAutosaveAt;
        public double CreatedAt;        // for stuck-session safety net
        public bool EverAccessed;       // did WasAccessing ever go true?
        public int Rows;                // for the save-file round-trip
    }

    /// <summary>Grace window — if a session never transitions to "accessing"
    /// within this many seconds, assume the open RPC was dropped and force
    /// cleanup so the player isn't blocked from retrying.</summary>
    private const double StuckSessionTimeoutSeconds = 10.0;

    private Config _cfg;
    private readonly Dictionary<int, Session> _sessions = new Dictionary<int, Session>();
    private TimerHandle _tick;
    private string _saveDir;

    // ============================================================ OnLoad / OnUnload

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        _saveDir = Path.Combine(StyxCore.DataPath(), "StyxBackpack");
        Directory.CreateDirectory(_saveDir);

        // Register every perm with PermEditor — the tier perms AND the
        // keep-on-death perm show up in the plugin-filtered slice.
        foreach (var t in _cfg.SizeByPerm)
        {
            if (string.IsNullOrEmpty(t?.Perm)) continue;
            StyxCore.Perms.RegisterKnown(t.Perm,
                string.Format("Open a backpack ({0} slots)", t.Rows * _cfg.Cols), Name);
        }
        StyxCore.Perms.RegisterKnown(_cfg.KeepOnDeathPerm,
            "Backpack contents persist through death (otherwise dropped as loot bag)", Name);

        // Launcher entry — only players with a size tier see it (resolved
        // at render time by the permission gate on the most-permissive
        // use-perm). For a multi-tier check we use the *lowest* tier perm
        // in the config as the gate; any higher tier implies lower ones
        // anyway via group membership.
        string gatePerm = _cfg.SizeByPerm.Count > 0
            ? _cfg.SizeByPerm[_cfg.SizeByPerm.Count - 1].Perm   // lowest tier = broadest gate
            : "styx.backpack.use";
        Styx.Ui.Menu.Register(this, "Backpack  /b", OpenFor, permission: gatePerm);

        StyxCore.Commands.Register("b",
            "Open your persistent backpack — /b [status|clear|drop|sweep]",
            (ctx, args) =>
            {
                if (ctx.Client == null) { ctx.Reply("Run this in-game."); return; }
                var p = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
                if (p == null) return;
                var pid = StyxCore.Player.PlatformIdOf(p);

                if (args.Length > 0)
                {
                    var sub = args[0].ToLowerInvariant();
                    switch (sub)
                    {
                        case "status":
                            CmdStatus(ctx, p, pid);
                            return;
                        case "clear":
                            CmdClear(ctx, p, pid);
                            return;
                        case "drop":
                            CmdDropTest(ctx, p, pid);
                            return;
                        case "sweep":
                            CmdSweep(ctx, p, pid);
                            return;
                        default:
                            ctx.Reply("[ffaa00][Backpack] Unknown subcommand. Try: status, clear, drop, sweep[-]");
                            return;
                    }
                }

                OpenFor(p);
            });

        _tick = Scheduler.Every(Math.Max(0.1, _cfg.PollSeconds), Tick, name: "StyxBackpack.tick");

        Log.Out("[StyxBackpack] Loaded v0.3.0 — {0} size tier(s), keep-on-death perm '{1}', data dir: {2}",
            _cfg.SizeByPerm.Count, _cfg.KeepOnDeathPerm, _saveDir);
    }

    public override void OnUnload()
    {
        if (_tick != null) { _tick.Destroy(); _tick = null; }
        // Flush every open session to disk before tearing down — otherwise
        // a hot-reload loses in-progress changes.
        foreach (var s in _sessions.Values)
        {
            try { SaveSession(s); } catch (Exception e) { Log.Warning("[StyxBackpack] Flush-on-unload failed for " + s.Pid + ": " + e.Message); }
            try { DespawnBackpackEntity(s.BackpackEntityId); } catch { }
        }
        _sessions.Clear();
        Styx.Ui.Menu.UnregisterAll(this);
        StyxCore.Perms.UnregisterKnownByOwner(Name);
    }

    // ============================================================ /b subcommands

    /// <summary>Inspect — tier perm, slot count, save-file presence + size,
    /// keep-on-death status. Pure read; safe for any player.</summary>
    private void CmdStatus(Styx.Commands.CommandContext ctx, EntityPlayer p, string pid)
    {
        var tier = ResolveTierFor(pid);
        bool keep = HasKeepOnDeath(pid);
        var path = SaveFilePath(pid);
        bool hasFile = File.Exists(path);
        long fileBytes = hasFile ? new FileInfo(path).Length : 0;
        int savedItems = 0;
        if (hasFile) { try { savedItems = LoadItemsList(path).Count; } catch { } }

        ctx.Reply(string.Format("[ccddff][Backpack] tier:[-] [ffffdd]{0}[-] ({1}×{2} = {3} slots)",
            tier?.Perm ?? "(none)",
            tier?.Rows ?? 0, _cfg.Cols, (tier?.Rows ?? 0) * _cfg.Cols));
        ctx.Reply(string.Format("[ccddff][Backpack] keep-on-death:[-] {0}",
            keep ? "[00ff66]YES[-]" : "[888888]no[-] (drops as loot bag on death)"));
        ctx.Reply(string.Format("[ccddff][Backpack] save:[-] {0} ({1} bytes, {2} item stacks)",
            hasFile ? "[00ff66]exists[-]" : "[888888]none yet[-]", fileBytes, savedItems));
        if (_sessions.TryGetValue(p.entityId, out var s))
            ctx.Reply(string.Format("[ccddff][Backpack] currently OPEN[-] entityId={0}", s.BackpackEntityId));
    }

    /// <summary>Wipe-self — delete the player's own save file. Used as the
    /// "I don't want my stash carrying across wipe" escape hatch the original
    /// spec called for. Refuses while the backpack is open (avoids a session
    /// holding a stale s.Loot then writing it back over the file we just
    /// deleted on the next autosave tick).</summary>
    private void CmdClear(Styx.Commands.CommandContext ctx, EntityPlayer p, string pid)
    {
        if (_sessions.ContainsKey(p.entityId))
        {
            ctx.Reply("[ffaa00][Backpack] Close the backpack first, then re-run /b clear.[-]");
            return;
        }
        var path = SaveFilePath(pid);
        if (!File.Exists(path))
        {
            ctx.Reply("[888888][Backpack] Nothing to clear — no save file.[-]");
            return;
        }
        try
        {
            File.Delete(path);
            ctx.Reply("[00ff66][Backpack] Save file deleted. Next /b will open an empty backpack.[-]");
            Log.Out("[StyxBackpack] {0} cleared their own save file via /b clear", pid);
        }
        catch (Exception e)
        {
            ctx.Reply("[ff6666][Backpack] Delete failed: " + e.Message + "[-]");
        }
    }

    /// <summary>Test the drop-on-death code path WITHOUT actually dying.
    /// Spawns the player's saved backpack contents as a loot bag at their
    /// current position, then deletes the save file — same flow that
    /// OnEntityDeath runs for non-keep-on-death players. Lets us validate
    /// the death drop end-to-end safely.</summary>
    private void CmdDropTest(Styx.Commands.CommandContext ctx, EntityPlayer p, string pid)
    {
        if (_sessions.ContainsKey(p.entityId))
        {
            ctx.Reply("[ffaa00][Backpack] Close the backpack first, then re-run /b drop.[-]");
            return;
        }
        var path = SaveFilePath(pid);
        if (!File.Exists(path))
        {
            ctx.Reply("[888888][Backpack] No save file to drop.[-]");
            return;
        }
        try
        {
            var items = LoadItemsList(path);
            if (items.Count == 0)
            {
                ctx.Reply("[888888][Backpack] Save file exists but contains no items.[-]");
                return;
            }
            var dropPos = p.GetPosition() + Vector3.up * 0.5f;
            SpawnDropBag(dropPos, items);
            File.Delete(path);
            ctx.Reply(string.Format("[00ff66][Backpack] Dropped {0} stack(s) as loot bag at your feet — save file cleared.[-]",
                items.Count));
            Log.Out("[StyxBackpack] {0} drop-test: {1} stacks dropped, save cleared", pid, items.Count);
        }
        catch (Exception e)
        {
            ctx.Reply("[ff6666][Backpack] Drop test failed: " + e.Message + "[-]");
        }
    }


    /// <summary>Admin cleanup — manually triggers the orphan sweep without
    /// waiting for a server restart. Useful if a crash left orphan stash
    /// bags lying around. Gated behind the keep-on-death perm as a rough
    /// "trusted user" check; could be its own perm if needed.</summary>
    private void CmdSweep(Styx.Commands.CommandContext ctx, EntityPlayer p, string pid)
    {
        // Use the existing admin perm (any admin-tier user) as the gate.
        if (!StyxCore.Perms.HasPermission(pid, "styx.perm.admin"))
        {
            ctx.Reply("[ff6666][Backpack] /b sweep requires styx.perm.admin.[-]");
            return;
        }
        int n = SweepOrphanStashBags();
        ctx.Reply(string.Format("[00ff66][Backpack] Swept {0} orphan stash bag(s).[-]", n));
    }

    /// <summary>
    /// Scan every spawned EntityBackpack and despawn those that match our
    /// stash signature (bPlayerBackpack=true AND RefPlayerId=-1). This
    /// pair distinguishes us from:
    ///   - Vanilla death bags:  bPlayerBackpack=true,  RefPlayerId>0
    ///   - ZombieLoot drops:    bPlayerBackpack=false, RefPlayerId=-1
    ///   - Other mods' bags:    unknown — we rely on them not matching both
    ///
    /// Skips bags currently in an active session — we're the ones using them,
    /// and `lockedTileEntities` protects them from the engine's auto-remove.
    ///
    /// Called from:
    ///   - OnServerInitialized  (sweep any orphans left by the previous boot)
    ///   - /b sweep              (admin-triggered immediate cleanup)
    /// </summary>
    private int SweepOrphanStashBags()
    {
        var world = GameManager.Instance?.World;
        if (world?.Entities?.list == null) return 0;

        // Snapshot session entity ids so we don't nuke the live containers
        // of players who currently have /b open.
        var activeIds = new HashSet<int>();
        foreach (var s in _sessions.Values)
            if (s != null) activeIds.Add(s.BackpackEntityId);

        // Copy the entity list before iterating — RemoveEntity mutates it.
        var snapshot = new List<Entity>(world.Entities.list.Count);
        foreach (var e in world.Entities.list) snapshot.Add(e);

        int removed = 0;
        foreach (var e in snapshot)
        {
            if (!(e is EntityBackpack bag)) continue;
            if (activeIds.Contains(bag.entityId)) continue;
            if (bag.RefPlayerId != -1) continue;                       // has a player owner (death bag) — skip
            if (bag.lootContainer == null) continue;
            if (!bag.lootContainer.bPlayerBackpack) continue;          // zombie-loot bag or similar — skip
            try
            {
                // Empty the items[] array first -- engine treats non-empty
                // bPlayerBackpack=true bags as persistent and chunk-save
                // re-persists them after RemoveEntity. With empty items
                // the engine's auto-Kill path fires cleanly. Items in
                // these orphan bags are safe to drop -- they're either
                // duplicates of what's in the player's JSON save (the
                // bag is leftover from a previous session that already
                // saved) or junk from a stale /b session.
                if (bag.lootContainer.items != null)
                {
                    for (int i = 0; i < bag.lootContainer.items.Length; i++)
                        bag.lootContainer.items[i] = ItemStack.Empty.Clone();
                    bag.lootContainer.SetModified();
                }
                world.RemoveEntity(bag.entityId, EnumRemoveEntityReason.Despawned);
                removed++;
            }
            catch (Exception ex)
            {
                Log.Warning("[StyxBackpack] Orphan sweep: failed to remove " + bag.entityId + ": " + ex.Message);
            }
        }
        if (removed > 0) Log.Out("[StyxBackpack] Orphan sweep removed {0} stash bag(s)", removed);
        return removed;
    }

    /// <summary>Server-init hook — run the orphan sweep once after the
    /// world and entities are fully loaded. Catches orphans left by crashes
    /// or unclean shutdowns on the previous session.</summary>
    void OnServerInitialized()
    {
        try { SweepOrphanStashBags(); }
        catch (Exception e) { Log.Warning("[StyxBackpack] Startup sweep failed: " + e.Message); }
    }

    // ============================================================ tier resolution

    /// <summary>Walk SizeByPerm top-down — first perm the player has wins.
    /// Returns null if no tier matches.</summary>
    private SizeTier ResolveTierFor(string pid)
    {
        if (string.IsNullOrEmpty(pid)) return null;
        foreach (var t in _cfg.SizeByPerm)
        {
            if (string.IsNullOrEmpty(t?.Perm)) continue;
            if (StyxCore.Perms.HasPermission(pid, t.Perm)) return t;
        }
        return null;
    }

    private bool HasKeepOnDeath(string pid) =>
        !string.IsNullOrEmpty(_cfg.KeepOnDeathPerm) &&
        StyxCore.Perms.HasPermission(pid, _cfg.KeepOnDeathPerm);

    // ============================================================ open flow

    private void OpenFor(EntityPlayer p)
    {
        if (p == null) return;
        if (!_cfg.Enabled) { Styx.Server.Whisper(p, "[ffaa00][Backpack] Disabled in config.[-]"); return; }

        var pid = StyxCore.Player.PlatformIdOf(p);
        var tier = ResolveTierFor(pid);
        if (tier == null)
        {
            Styx.Server.Whisper(p, "[ff6666][Backpack] You don't have any backpack-tier permission.[-]");
            return;
        }

        // One session per player at a time. If the UI is already open,
        // just force-refocus rather than spawning a second entity — stops
        // spam-clicks from orphaning backpack entities on the map.
        if (_sessions.TryGetValue(p.entityId, out var existing))
        {
            Styx.Server.Whisper(p, "[ccddff][Backpack] Already open (close it first).[-]");
            return;
        }

        try
        {
            // Spawn at the player's position. Two earlier v0.2 attempts tried
            // to hide the bag by spawning it 50m underground, with increasing
            // levels of physics hackery:
            //   - Plain y-50 spawn → bag fell through the world within 1.5s,
            //     engine's "below world" relocator yanked it to the surface
            //     mid-open, broke the TELockServer handshake (stuck session).
            //   - y-50 + isKinematic=true on every Rigidbody → no-op. The
            //     engine assigns the rigidbody in its own Start() which runs
            //     AFTER our code here, overwriting our kinematic flag. Bag
            //     still fell.
            // Visible flicker at the player's feet is the stable path. The
            // bag is locked to the player via TELockServer while in use, so
            // other players can't loot it. Accepting the visibility trade.
            Vector3 pos = p.GetPosition();
            // Use our custom StyxBackpack entity_class (defined in
            // Config/entityclasses.xml + Localization.txt). Same C# class
            // (EntityBackpack), same visuals, but the loot window header
            // reads "Styx Backpack" so players can distinguish their
            // persistent stash from vanilla death bags + Styx sell bins.
            // Falls back to vanilla "Backpack" if the modlet didn't load.
            var entity = EntityFactory.CreateEntity("StyxBackpack".GetHashCode(), pos) as EntityBackpack;
            if (entity == null)
            {
                Log.Warning("[StyxBackpack] StyxBackpack entity_class not found -- falling back to vanilla Backpack. Did Config/entityclasses.xml load?");
                entity = EntityFactory.CreateEntity("Backpack".GetHashCode(), pos) as EntityBackpack;
            }
            if (entity == null)
            {
                Log.Warning("[StyxBackpack] Couldn't create Backpack entity for " + pid);
                Styx.Server.Whisper(p, "[ff6666][Backpack] Failed to create container entity (see log).[-]");
                return;
            }

            // Hydrate the loot container from the on-disk save (if any).
            var loot = BuildContainer(entity, tier.Rows, _cfg.Cols);
            LoadStacksInto(pid, loot, tier.Rows, _cfg.Cols);

            // bPlayerBackpack = TRUE is essential for persistence.
            // GameManager.DestroyLootOnClose picks a branch based on this flag:
            //   bPlayerBackpack=false → DropContentOfLootContainerServer(...)
            //     spills every item into the world as ground pickups, THEN
            //     kills the entity. That's the catastrophic data-loss path —
            //     by the time our close-poll runs the items are already
            //     scattered on the floor.
            //   bPlayerBackpack=true → if (!IsEmpty()) return; else Kill.
            //     Non-empty bags are left entirely alone (we save + despawn
            //     ourselves via Tick). Empty bags get a harmless Kill.
            // The name is misleading — setting this true does NOT turn our
            // container into a vanilla death bag (that's driven by
            // RefPlayerId>0 and the 21-day nav-marker path). It only changes
            // the close-destroy branch, which is exactly what we need.
            loot.bPlayerBackpack = true;
            // bTouched=true stops the engine auto-fill path from running.
            loot.bTouched = true;
            loot.SetModified();

            entity.RefPlayerId = -1;  // no vanilla "owned death bag" nav-marker

            // IMPORTANT: do NOT set `id = -1` here. The engine's spawn path
            // (EntityFactory.CreateEntity(ECD) at line 164) treats id=-1 as
            // "assign a fresh entityId via nextEntityID++" — which would
            // orphan the id we cached on the local `entity` and leave us
            // pointing at a non-existent entity for TELockServer/despawn.
            // By preserving the pre-assigned id (EntityCreationData(entity)
            // copies it), the spawned entity in the world ends up with the
            // same entityId we already captured. See line 170 of EntityFactory
            // which just advances nextEntityID past the reused id — safe.
            int preassignedId = entity.entityId;
            var ecd = new EntityCreationData(entity)
            {
                lootContainer = loot,
            };
            entity.OnEntityUnload();
            GameManager.Instance.RequestToSpawnEntityServer(ecd);

            double sysNow = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            var session = new Session
            {
                PlayerEntityId = p.entityId,
                Pid = pid,
                BackpackEntityId = preassignedId,
                Loot = loot,
                EntityPos = new Vector3i(pos),
                WasAccessing = false,
                LastAutosaveAt = 0,
                CreatedAt = sysNow,
                EverAccessed = false,
                Rows = tier.Rows,
            };
            _sessions[p.entityId] = session;

            Scheduler.Once(0.2, () => ForceOpenFor(session), name: "StyxBackpack.forceOpen");

            Log.Out("[StyxBackpack] Opened for {0} ({1}) — {2}×{3} slots, entityId={4}",
                p.EntityName, pid, tier.Rows, _cfg.Cols, entity.entityId);
        }
        catch (Exception e)
        {
            Log.Warning("[StyxBackpack] OpenFor failed: " + e.Message + "\n" + e.StackTrace);
            Styx.Server.Whisper(p, "[ff6666][Backpack] Open failed: " + e.Message + "[-]");
        }
    }

    /// <summary>Force the player's client to open the loot UI on our
    /// spawned entity. Uses the engine's server-to-client lock-and-open
    /// RPC — same pathway the vanilla "E on a container" flow takes
    /// after the server grants access.</summary>
    private void ForceOpenFor(Session s)
    {
        try
        {
            var gm = GameManager.Instance;
            if (gm == null) return;

            // Double-check the entity actually made it into the world
            // table — spawn is async. If not present, bail cleanly.
            var entity = gm.World?.GetEntity(s.BackpackEntityId);
            if (entity == null)
            {
                Log.Warning("[StyxBackpack] Spawned entity " + s.BackpackEntityId + " not found post-spawn — aborting session");
                _sessions.Remove(s.PlayerEntityId);
                return;
            }
            // TELockServer args: (clusterIdx, blockPos, lootEntityId, openerEntityId).
            // blockPos is ignored when lootEntityId != -1 (World.GetTileEntity(int)
            // resolves via entity table) — we pass entity's current pos just in case.
            gm.TELockServer(0, s.EntityPos, s.BackpackEntityId, s.PlayerEntityId);
            // TELockServer adds the TE to GameManager.lockedTileEntities —
            // that dictionary is our actual open/close signal (see Tick()).
            // The TileEntityLootContainer.IsUserAccessing flag is just a
            // local bool that the engine DOESN'T flip back on
            // TEUnlockServer, so polling that would never detect the close.
            //
            // CRITICAL: now that TELockServer has registered us in
            // lockedTileEntities, release the IsUserAccessing flag we set
            // pre-spawn. Two reasons it MUST go false now:
            //
            //   1. NetPackageTileEntity.ProcessPackage → TileEntityLootContainer.read
            //      (line 178 of TileEntityLootContainer.cs) reads incoming
            //      slot updates from the client into a *throwaway* ItemStack
            //      variable when `bUserAccessing == true` on the server. The
            //      engine treats client-side as authoritative-in-progress and
            //      discards all item sync to avoid conflicts. Leaving the flag
            //      true means EVERY drag-drop the player makes is silently
            //      thrown away by the server. With it false, the read path
            //      writes into items[] as expected.
            //
            //   2. Auto-remove protection rolls over to the `!flag` (locked-
            //      table) check now that we're locked. We don't need the
            //      IsUserAccessing shield anymore.
            s.Loot.SetUserAccessing(_bUserAccessing: false);
        }
        catch (Exception e)
        {
            Log.Warning("[StyxBackpack] ForceOpenFor failed: " + e.Message);
        }
    }

    /// <summary>Build an empty TileEntityLootContainer sized for the tier.
    /// See ZombieLoot v0.4 comments for why lootListName points at a
    /// valid-but-already-touched list (client-side NPE avoidance).
    ///
    /// We deliberately leave IsUserAccessing=true on return. Reason:
    /// EntityBackpack.OnUpdateEntity (decomp lines 79-81) auto-despawns
    /// the entity within a few ticks if:
    ///   lootContainer.bTouched && lootContainer.IsEmpty() && !IsUserAccessing()
    ///   && !(this TE is in GameManager.lockedTileEntities)
    /// We set bTouched=true to block vanilla auto-fill, so every fresh
    /// (empty) backpack would trip this and vanish during the 0.2s gap
    /// before ForceOpenFor runs TELockServer. Holding IsUserAccessing=true
    /// short-circuits that check. ForceOpenFor then hands the access lock
    /// over to the player via TELockServer, which puts us in the
    /// lockedTileEntities table — keeping the despawn blocked permanently
    /// until the player closes.</summary>
    private TileEntityLootContainer BuildContainer(EntityBackpack entity, int rows, int cols)
    {
        var loot = new TileEntityLootContainer((Chunk)null);
        // Re-use the backpack's default list name so XUiC_LootWindowGroup's
        // un-guarded `GetLootContainer(lootListName).openTime` lookup doesn't
        // NPE on the client. bTouched=true (set by SetEmpty) then prevents
        // any server-side regeneration on top of our hydrated items.
        string listName = entity.GetLootList();
        loot.lootListName = listName;
        loot.SetUserAccessing(_bUserAccessing: true);
        loot.SetEmpty();

        // Resize to the tier grid. clearItems=true rebuilds the items[]
        // array at the new length — safe because we haven't populated yet.
        loot.SetContainerSize(new Vector2i(cols, rows), clearItems: true);

        // INTENTIONALLY NOT calling SetUserAccessing(false) — see method
        // docstring. The flag is released implicitly when the player closes
        // and the engine's TEUnlockServer path runs.
        return loot;
    }

    // ============================================================ tick — close detection + autosave

    private void Tick()
    {
        if (_sessions.Count == 0) return;

        double sysNow = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        var gm = GameManager.Instance;
        var locked = gm?.lockedTileEntities;
        var world = gm?.World;

        // Snapshot the session list — we mutate _sessions inside.
        List<Session> toClose = null;
        foreach (var s in _sessions.Values)
        {
            // Re-fetch the live container reference from the world entity table
            // every tick. Our originally-cached `s.Loot` reference may be stale
            // — the engine's spawn/sync pipeline can replace `entity.lootContainer`
            // with a fresh instance, leaving our reference orphaned and reading
            // an empty items[] no matter what the player puts in the UI.
            var entity = world?.GetEntity(s.BackpackEntityId);
            var liveLoot = entity?.lootContainer;
            if (liveLoot != null && !ReferenceEquals(liveLoot, s.Loot))
            {
                Log.Out("[StyxBackpack] Container reference replaced for {0} — refetching (was hash {1}, now {2})",
                    s.Pid, s.Loot?.GetHashCode() ?? 0, liveLoot.GetHashCode());
                s.Loot = liveLoot;
            }

            // Our signal that the player still has the UI open is the
            // engine's server-side lock table — TELockServer put our TE in,
            // TEUnlockServer removes it when the client closes. Note we can
            // NOT use IsUserAccessing() here (see ForceOpenFor comment).
            bool accessingNow = locked != null && s.Loot != null && locked.ContainsKey(s.Loot);

            if (accessingNow) s.EverAccessed = true;

            // Close detection: last tick was accessing, this tick isn't.
            if (s.WasAccessing && !accessingNow)
            {
                (toClose ??= new List<Session>()).Add(s);
            }
            else if (accessingNow)
            {
                // Autosave while open.
                if (sysNow - s.LastAutosaveAt >= _cfg.AutosaveSeconds)
                {
                    try { SaveSession(s); s.LastAutosaveAt = sysNow; }
                    catch (Exception e) { Log.Warning("[StyxBackpack] Autosave failed for " + s.Pid + ": " + e.Message); }
                }
            }
            else if (!s.EverAccessed && sysNow - s.CreatedAt > StuckSessionTimeoutSeconds)
            {
                // Session never transitioned to "accessing" in the grace
                // window — open RPC was dropped or denied. Clean up so
                // the player isn't locked out from retrying.
                Log.Warning("[StyxBackpack] Session for " + s.Pid + " never opened — cleaning up stuck session");
                (toClose ??= new List<Session>()).Add(s);
            }

            s.WasAccessing = accessingNow;
        }

        if (toClose != null)
        {
            foreach (var s in toClose)
            {
                try { SaveSession(s); } catch (Exception e) { Log.Warning("[StyxBackpack] Close-save failed for " + s.Pid + ": " + e.Message); }
                // Empty the in-world loot container BEFORE despawn. Engine
                // treats EntityBackpack with bPlayerBackpack=true and
                // !IsEmpty() as a persistent player backpack -- chunk save
                // re-persists it even after RemoveEntity, leading to orphan
                // bags on next connect. Items are already in the JSON save
                // (SaveSession ran above), so clearing items[] here is
                // lossless. With an empty container the engine's own
                // auto-Kill path fires cleanly and the entity is removed
                // from chunk save.
                try { ClearLootContainerItems(s); } catch (Exception e) { Log.Warning("[StyxBackpack] Close-clear failed for " + s.Pid + ": " + e.Message); }
                try { DespawnBackpackEntity(s.BackpackEntityId); } catch { }
                _sessions.Remove(s.PlayerEntityId);
                Log.Out("[StyxBackpack] Closed for {0} (entityId={1})", s.Pid, s.BackpackEntityId);
            }
        }
    }

    /// <summary>
    /// Empty the session's in-world loot container so the engine's
    /// despawn path treats it as a removable empty backpack rather than a
    /// persistent player stash. Caller is responsible for SaveSession
    /// having already written items to JSON before this runs -- otherwise
    /// items are lost.
    /// </summary>
    private void ClearLootContainerItems(Session s)
    {
        // Re-fetch the live container -- engine may have swapped it.
        var world = GameManager.Instance?.World;
        var entity = world?.GetEntity(s.BackpackEntityId);
        var loot = entity?.lootContainer ?? s.Loot;
        if (loot?.items == null) return;
        for (int i = 0; i < loot.items.Length; i++)
            loot.items[i] = ItemStack.Empty.Clone();
        loot.SetModified();
    }

    private void DespawnBackpackEntity(int entityId)
    {
        var world = GameManager.Instance?.World;
        if (world == null) return;
        var ent = world.GetEntity(entityId);
        if (ent != null)
            world.RemoveEntity(entityId, EnumRemoveEntityReason.Despawned);
    }

    // ============================================================ save / load

    private string SaveFilePath(string pid) =>
        Path.Combine(_saveDir, SanitizePid(pid) + ".json");

    /// <summary>Strip anything that isn't filesystem-safe. PlatformIds
    /// from Steam ("Steam_76561198...") are already safe; from EGS/console
    /// we might see odd chars. Defensive-only.</summary>
    private static string SanitizePid(string pid)
    {
        if (string.IsNullOrEmpty(pid)) return "unknown";
        var sb = new System.Text.StringBuilder(pid.Length);
        foreach (var c in pid)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        return sb.ToString();
    }

    private void SaveSession(Session s)
    {
        if (s?.Loot == null || string.IsNullOrEmpty(s.Pid)) return;
        byte[] blob;
        int nonEmpty = 0;
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            var items = s.Loot.items ?? Array.Empty<ItemStack>();
            bw.Write(items.Length);
            foreach (var it in items)
            {
                var stack = it ?? ItemStack.Empty.Clone();
                if (!stack.IsEmpty()) nonEmpty++;
                stack.Write(bw);
            }
            bw.Flush();
            blob = ms.ToArray();
        }
        // Autosave is silent in steady state — flip _verboseSave to true
        // (or live-edit the config below) for diagnostics.
        if (_cfg.VerboseAutosave)
            Log.Out("[StyxBackpack] SaveSession {0}: {1} slots, {2} non-empty, {3} bytes",
                s.Pid, s.Loot.items?.Length ?? 0, nonEmpty, blob.Length);

        var save = new SaveFile
        {
            SchemaVersion = 1,
            SavedAtUnix = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds,
            Rows = s.Rows,
            Cols = _cfg.Cols,
            StacksB64 = Convert.ToBase64String(blob),
        };

        var json = JsonConvert.SerializeObject(save, Formatting.Indented);
        // Write to a temp file then rename — atomic save, no half-written
        // files if the server crashes mid-write.
        var path = SaveFilePath(s.Pid);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    private void LoadStacksInto(string pid, TileEntityLootContainer loot, int rows, int cols)
    {
        var path = SaveFilePath(pid);
        if (!File.Exists(path))
        {
            Log.Out("[StyxBackpack] LoadStacksInto {0}: no save file at {1} — fresh empty backpack", pid, path);
            return;
        }

        SaveFile save;
        try { save = JsonConvert.DeserializeObject<SaveFile>(File.ReadAllText(path)); }
        catch (Exception e)
        {
            Log.Warning("[StyxBackpack] Save file for " + pid + " unreadable: " + e.Message + " — starting empty");
            return;
        }
        if (save == null || string.IsNullOrEmpty(save.StacksB64))
        {
            Log.Out("[StyxBackpack] LoadStacksInto {0}: save file empty/null — starting empty", pid);
            return;
        }

        int loaded = 0;
        try
        {
            var blob = Convert.FromBase64String(save.StacksB64);
            using var ms = new MemoryStream(blob);
            using var br = new BinaryReader(ms);

            int savedLen = br.ReadInt32();
            int slots = rows * cols;
            int copy = Math.Min(savedLen, slots);
            for (int i = 0; i < savedLen; i++)
            {
                var stack = new ItemStack();
                stack.Read(br);
                // If the saved grid was bigger than the player's current tier
                // (e.g. admin demoted from master → vip), drop overflow items.
                // Logged so admins can spot the demotion causing loss.
                if (i < copy && i < loot.items.Length)
                {
                    loot.items[i] = stack;
                    if (!stack.IsEmpty()) loaded++;
                }
                else if (!stack.IsEmpty())
                    Log.Warning(string.Format(
                        "[StyxBackpack] Dropped overflow slot {0} for {1} ({2}×{3}) — tier shrunk since last save",
                        i, pid, stack.count, stack.itemValue?.ItemClass?.Name ?? "?"));
            }
            Log.Out("[StyxBackpack] LoadStacksInto {0}: loaded {1} non-empty stack(s) from {2}", pid, loaded, path);
        }
        catch (Exception e)
        {
            Log.Warning("[StyxBackpack] Load failed for " + pid + ": " + e.Message + " — starting empty");
        }
    }

    // ============================================================ death handling

    /// <summary>On player death, check keep-on-death perm. Without it,
    /// we drop all saved items as a loot bag at the death position and
    /// clear the file so the player respawns with a fresh empty backpack.
    ///
    /// Defensive: also fired by some engine cleanup paths (entity unload,
    /// disconnect-induced cleanup) where the player isn't actually dead.
    /// We gate on IsDead() + Health<=0 to be sure — if the player walks
    /// off, those return false and we skip the drop entirely.</summary>
    void OnEntityDeath(EntityAlive victim)
    {
        if (!(victim is EntityPlayer ep)) return;
        var pid = StyxCore.Player.PlatformIdOf(ep);
        if (string.IsNullOrEmpty(pid)) return;

        // Disconnect / unload guard. If player Health > 0 OR IsDead is false,
        // this isn't a real death — bail out before doing anything destructive.
        // This was added to fix "/b stash bag drops on logoff" — the engine
        // was firing OnEntityDeath through an unload path with the player
        // still alive (Health > 0).
        bool reallyDead = victim.IsDead() || (ep.Stats?.Health?.Value ?? 100f) <= 0f;
        if (!reallyDead)
        {
            Log.Out("[StyxBackpack] OnEntityDeath fired for {0} but player not dead (IsDead={1}, Health={2}) — skipping drop",
                pid, victim.IsDead(), ep.Stats?.Health?.Value ?? -1f);
            return;
        }

        // If the backpack is currently OPEN for this player, close + save
        // first so we're dropping the actual current contents.
        if (_sessions.TryGetValue(ep.entityId, out var s))
        {
            try { SaveSession(s); } catch { }
            try { ClearLootContainerItems(s); } catch { }
            try { DespawnBackpackEntity(s.BackpackEntityId); } catch { }
            _sessions.Remove(ep.entityId);
        }

        if (HasKeepOnDeath(pid))
        {
            Log.Out("[StyxBackpack] {0} died WITH keep-on-death — save preserved", pid);
            return;
        }

        // Load saved items and drop them as a bag. Skip entirely if the
        // file doesn't exist (player never used the backpack).
        var path = SaveFilePath(pid);
        if (!File.Exists(path))
        {
            Log.Out("[StyxBackpack] {0} died without keep-on-death — no save file to drop", pid);
            return;
        }

        try
        {
            var items = LoadItemsList(path);
            if (items.Count > 0)
            {
                var dropPos = ep.GetPosition() + Vector3.up * 0.5f;
                SpawnDropBag(dropPos, items);
            }
            File.Delete(path);
            Log.Out("[StyxBackpack] {0} died without keep-on-death — dropped {1} stack(s)", pid, items.Count);
        }
        catch (Exception e)
        {
            Log.Warning("[StyxBackpack] Death-drop failed for " + pid + ": " + e.Message);
        }
    }

    private List<ItemStack> LoadItemsList(string path)
    {
        var result = new List<ItemStack>();
        SaveFile save;
        try { save = JsonConvert.DeserializeObject<SaveFile>(File.ReadAllText(path)); }
        catch { return result; }
        if (save == null || string.IsNullOrEmpty(save.StacksB64)) return result;

        var blob = Convert.FromBase64String(save.StacksB64);
        using var ms = new MemoryStream(blob);
        using var br = new BinaryReader(ms);
        int len = br.ReadInt32();
        for (int i = 0; i < len; i++)
        {
            var stack = new ItemStack();
            stack.Read(br);
            if (!stack.IsEmpty()) result.Add(stack);
        }
        return result;
    }

    /// <summary>Spawn a transient loot bag with our items, same pattern
    /// as ZombieLoot v0.4 (bPlayerBackpack=false, RefPlayerId=-1, schedule
    /// despawn via position scan).</summary>
    private void SpawnDropBag(Vector3 pos, List<ItemStack> items)
    {
        var gm = GameManager.Instance;
        if (gm == null) return;

        var entity = EntityFactory.CreateEntity("Backpack".GetHashCode(), pos) as EntityBackpack;
        if (entity == null) return;

        var loot = new TileEntityLootContainer((Chunk)null);
        string listName = entity.GetLootList();
        loot.lootListName = listName;
        loot.SetUserAccessing(_bUserAccessing: true);
        loot.SetEmpty();

        var lc = LootContainer.GetLootContainer(listName);
        if (lc != null) loot.SetContainerSize(lc.size);

        foreach (var s in items)
        {
            if (s == null || s.IsEmpty()) continue;
            loot.AddItem(s.Clone());
        }

        loot.bTouched = true;
        loot.SetUserAccessing(_bUserAccessing: false);
        loot.SetModified();

        entity.RefPlayerId = -1;

        var ecd = new EntityCreationData(entity) { id = -1, lootContainer = loot };
        entity.OnEntityUnload();
        gm.RequestToSpawnEntityServer(ecd);

        if (_cfg.DropLifetimeSeconds > 0)
            ScheduleDropDespawn(pos, _cfg.DropLifetimeSeconds);
    }

    private void ScheduleDropDespawn(Vector3 pos, int seconds)
    {
        Scheduler.Once(seconds, () =>
        {
            try
            {
                var world = GameManager.Instance?.World;
                if (world == null) return;
                var nearby = StyxCore.World.EntitiesInRadius(pos, 1.5f);
                foreach (var e in nearby)
                {
                    if (!(e is EntityBackpack bag)) continue;
                    if (bag.RefPlayerId > 0) continue;  // player death bag — leave alone
                    world.RemoveEntity(bag.entityId, EnumRemoveEntityReason.Despawned);
                }
            }
            catch (Exception e) { Log.Warning("[StyxBackpack] Drop-bag despawn failed: " + e.Message); }
        }, name: "StyxBackpack.dropDespawn");
    }

    // ============================================================ disconnect cleanup

    /// <summary>Client went away — flush + despawn their session if open.
    /// Without this, a reconnecting player would find an orphan backpack
    /// entity floating at their old position with their saved contents.</summary>
    void OnPlayerDisconnected(ClientInfo ci, bool shuttingDown)
    {
        if (ci == null) return;
        var pid = ci.PlatformId?.CombinedString ?? "?";
        if (!_sessions.TryGetValue(ci.entityId, out var s))
        {
            // Useful diagnostic — confirms our hook is at least firing on logoff.
            Log.Out("[StyxBackpack] Disconnect for {0} — no active session, nothing to clean", pid);
            return;
        }

        try { SaveSession(s); } catch (Exception e) { Log.Warning("[StyxBackpack] Disconnect-save failed: " + e.Message); }
        // Empty the bag before despawn so chunk save doesn't re-persist
        // a non-empty player backpack -- same fix as Tick() close handler.
        try { ClearLootContainerItems(s); } catch (Exception e) { Log.Warning("[StyxBackpack] Disconnect-clear failed: " + e.Message); }
        try { DespawnBackpackEntity(s.BackpackEntityId); } catch { }
        _sessions.Remove(ci.entityId);
        Log.Out("[StyxBackpack] Disconnect cleanup for {0} (entityId={1})", s.Pid, s.BackpackEntityId);
    }

    /// <summary>
    /// Player connected — schedule an orphan-stash sweep at multiple
    /// short intervals as chunks stream in. The bag near the player
    /// becomes visible roughly when its chunk loads (~0.3-1s after
    /// spawn); subsequent ticks catch bags in chunks that load later.
    /// Earlier first-tick = less visible flicker for the player.
    ///
    /// Catches stash bags left behind by previous sessions where
    /// DespawnBackpackEntity raced the engine's chunk-save and the
    /// entity got persisted to disk instead of cleanly removed.
    /// New-session orphans should be rare since v0.3.1's
    /// ClearLootContainerItems-before-despawn fix, but the sweep
    /// remains as a safety net for old saves + unclean shutdowns.
    /// </summary>
    void OnPlayerSpawned(ClientInfo client, RespawnType reason, Vector3i pos)
    {
        if (client == null) return;
        // Only sweep on actual connects, not every respawn-on-death.
        if (reason != RespawnType.EnterMultiplayer && reason != RespawnType.JoinMultiplayer) return;

        // Schedule at 0.3s, 1s, 2s, 4s. The first catches the typical
        // "bag at the player's logoff position" case the moment its
        // chunk loads (usually well under a second). Later ticks catch
        // any bags whose chunks lag in. After 4s we stop -- if bags
        // are still in unloaded chunks, they're not visible to the
        // player anyway and the next /b sweep / server restart will
        // get them.
        ScheduleSweepAt(0.3,  "fast");
        ScheduleSweepAt(1.0,  "1s");
        ScheduleSweepAt(2.0,  "2s");
        ScheduleSweepAt(4.0,  "4s");
    }

    private void ScheduleSweepAt(double seconds, string tag)
    {
        Scheduler.Once(seconds, () =>
        {
            try
            {
                int n = SweepOrphanStashBags();
                if (n > 0)
                    Log.Out("[StyxBackpack] Post-connect sweep ({0}) removed {1} orphan stash bag(s)", tag, n);
            }
            catch (Exception e) { Log.Warning("[StyxBackpack] Post-connect sweep ({0}) failed: {1}", tag, e.Message); }
        }, name: "StyxBackpack.PostConnectSweep_" + tag);
    }

    // NOTE: an earlier attempt patched EntityBackpack.Start() Postfix to
    // override lootContainer.entityId to -1 in the hope that
    // XUiC_BackpackWindow.TryGetMoveDestinationInventory would accept the
    // backpack as a "block-based loot" destination (flag2 in that method)
    // and enable the inventory→container dump-buttons. The patch is
    // ineffective AND breaks close detection:
    //   1. ineffective -- TryGetMoveDestinationInventory is CLIENT-SIDE
    //      code. The server's lootContainer.entityId override doesn't
    //      reach the client, which runs its own EntityBackpack.Start()
    //      and re-stamps entityId from the entity itself. Server Harmony
    //      can't patch the client.
    //   2. breaks close detection -- TELockServer maps by
    //      tileEntityLootContainer.entityId. Mid-flight changes to that
    //      field disrupt IsUserAccessing / unlock lookups, so close
    //      events don't fire and the on-close autosave never runs. The
    //      next restart loads the pre-take state, "resurrecting" items.
    // The dump-button limitation is a fundamental constraint of being
    // server-only -- without a client companion DLL there's no way to
    // make the client-side TryGetMoveDestinationInventory accept an
    // entity-backed container. Players use shift-click on individual
    // stacks (which works for any open container) or drag-drop.

}
