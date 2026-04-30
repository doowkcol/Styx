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

// StyxZombieHealth v0.2 -- crosshair entity-health HUD readout.
// Shows the name + HP of the zombie (optional animals / players) the
// crosshair is currently aimed at, in a small panel below the compass.
//
// Targeting: multi-sample ray-vs-bounding-sphere from the player's eye,
// sampling at hips / chest / head heights to widen the effective hit
// volume. Single-point ray was too narrow for close-range targets.
// MaxRange is configurable (default 10m).
//
// UI plumbing: the entity name is pushed to the client via the
// indexed-localization pattern -- class hashes are mapped to small
// sequential indices (1..N) and the index is the cvar value, because
// 32-bit float cvars lose precision above 2^24 (~16.7M) and zombie
// class hashes are 9-digit ints. See STYX_CAPABILITIES.md sec 25 for
// the full pattern (crosshair entity targeting).
//
// Config: configs/StyxZombieHealth.json (auto-created with defaults).
//
// Perms:
//   styx.zhealth.use  -- see the HUD panel (default-on for everyone)
//
// Commands:
//   /zhealth status            -- show current config
//   /zhealth diag on|off       -- enable per-player diagnostic whisper
//   /zhealth probe             -- one-shot single-sample probe (debug)

using System;
using Styx;
using Styx.Plugins;
using Styx.Scheduling;
using UnityEngine;

[Info("StyxZombieHealth", "Doowkcol", "0.2.4")]
public class StyxZombieHealth : StyxPlugin
{
    public override string Description => "Crosshair entity-health HUD readout (zombies + optional animals/players)";

    // ============================================================ schema

    public class Config
    {
        public bool Enabled = true;

        /// <summary>Tick rate in seconds. 0.2 = 5Hz, smooth enough for HUD
        /// motion without taxing the server. Lower = smoother but heavier.</summary>
        public float TickSeconds = 0.2f;

        /// <summary>Maximum target distance in metres. Beyond this, no readout
        /// even if the player is aiming directly at the entity.</summary>
        public float MaxRange = 30f;

        /// <summary>Half-angle of the "looking at" cone in degrees. Used as
        /// a cheap pre-filter — entities within this angular cone of the
        /// look-vector go through the more accurate ray-vs-body-sphere test.
        /// Wider = catches more candidates (ray test is final arbiter).</summary>
        public float ConeAngleDegrees = 30f;

        /// <summary>Effective bounding-sphere radius for entities (metres).
        /// The look-ray must pass within this distance of the entity's torso
        /// to register as "aimed at". 0.7 ≈ a humanoid's chest+arms width.</summary>
        public float EntityHitRadius = 0.7f;

        /// <summary>True = require the player to have <see cref="Perm"/> to see
        /// the readout. False = visible to everyone (open feature).</summary>
        public bool RequirePerm = true;

        /// <summary>The perm checked when <see cref="RequirePerm"/> is true.</summary>
        public string Perm = "styx.zhealth.use";

        /// <summary>Show readout on zombies (the headline use case).</summary>
        public bool ShowZombies = true;

        /// <summary>Show readout on animals (deer, wolves, snakes, etc.).</summary>
        public bool ShowAnimals = false;

        /// <summary>Show readout on other players. Off by default — PvP servers
        /// usually consider this an unfair info advantage. Toggle on if you
        /// want a "duel HUD" feel.</summary>
        public bool ShowOtherPlayers = false;
    }

    // ============================================================ runtime

    private Config _cfg;
    private TimerHandle _tick;

    /// <summary>EntityClass int hash → small sequential label index (1..N).
    /// Indirection exists because cvars are 32-bit floats — they only
    /// represent integers exactly up to 2^24 (~16.7 million). Real
    /// EntityClass hashes are 9-digit ints (e.g. 633811747) which lose
    /// precision when round-tripped through a float cvar, causing the
    /// XUi binding to look up a NEAR-but-wrong key. Mapping to small
    /// sequential indices keeps every value well within float precision.
    /// Built once at OnLoad alongside the Labels.Register loop.</summary>
    private readonly System.Collections.Generic.Dictionary<int, int> _classIdToLabelIdx
        = new System.Collections.Generic.Dictionary<int, int>();

    /// <summary>Toggleable verbose log mode — flip via /zhealth diag on|off.
    /// Logs every target acquisition + loss transition per player so we can
    /// see exactly what the targeting code is doing without spamming at 5Hz.</summary>
    private static bool _diagLogs = false;

    /// <summary>Last target entityId per player. Used to log target
    /// acquired/lost TRANSITIONS only (not every tick) when diag is on.</summary>
    private readonly System.Collections.Generic.Dictionary<int, int> _lastTargetByPlayer
        = new System.Collections.Generic.Dictionary<int, int>();

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);

        if (_cfg.RequirePerm)
        {
            StyxCore.Perms.RegisterKnown(_cfg.Perm,
                "Show entity health HUD when looking at a zombie / animal / player",
                Name);
        }

        // Per-player cvars driving the XUi panel. Ephemeral.Register clears
        // them on player spawn so a stale "looking at" readout doesn't survive
        // a respawn cycle.
        //
        // styx.zhealth.classid = the EntityClass int id of the current target.
        // Maps to a label key "styx_zh_e_<classid>" via the XUi
        // {#localization('styx_zh_e_' + int(cvar(...)))} binding pattern.
        // Same trick StyxHud uses for ranks.
        Styx.Ui.Ephemeral.Register(
            "styx.zhealth.visible",
            "styx.zhealth.classid",
            "styx.zhealth.hp_curr",
            "styx.zhealth.hp_max",
            "styx.zhealth.hp_pct");

        // Pre-register a name label per EntityClass id. Labels are baked into
        // StyxRuntime/Config/Localization.txt at server shutdown and read
        // by the engine at next-boot init — so first deploy needs TWO restarts
        // to see names appear (first writes the label file, second loads it).
        // Subsequent edits to entity classes (modlets adding new zombies)
        // would need the same 2-restart cycle.
        int registered = RegisterEntityNameLabels();

        _tick = Scheduler.Every(_cfg.TickSeconds, Tick, "StyxZombieHealth.tick");

        // /zhealth — status + diag command. Status is open; diag toggle is open
        // too (it's a debug tool, not gameplay-affecting).
        StyxCore.Commands.Register("zhealth",
            "ZombieHealth — /zhealth [status|diag on|diag off|probe]",
            (ctx, args) => HandleCommand(ctx, args));

        Log.Out("[StyxZombieHealth] Loaded v0.2.4 — range={0}m cone={1}° tick={2}s perm={3} labels={4} (multi-sample, sequential-keys)",
            _cfg.MaxRange, _cfg.ConeAngleDegrees, _cfg.TickSeconds,
            _cfg.RequirePerm ? _cfg.Perm : "(open)", registered);
    }

    private void HandleCommand(Styx.Commands.CommandContext ctx, string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";

        if (sub == "diag" && args.Length >= 2)
        {
            _diagLogs = args[1].Equals("on", StringComparison.OrdinalIgnoreCase);
            ctx.Reply(string.Format("[00ff66]ZombieHealth diag: {0}[-]", _diagLogs ? "ON" : "off"));
            Log.Out("[StyxZombieHealth] Diag mode: {0}", _diagLogs ? "ON" : "off");
            return;
        }

        if (sub == "probe")
        {
            // One-shot scan for the calling player. Reports what was found.
            var p = ctx.Client != null
                ? GameManager.Instance?.World?.GetEntity(ctx.Client.entityId) as EntityPlayer
                : null;
            if (p == null) { ctx.Reply("[ff8888]Run this in-game.[-]"); return; }
            ProbePlayer(p, ctx);
            return;
        }

        // Default: status
        ctx.Reply(string.Format("[ccddff]ZombieHealth v0.2.1:[-] enabled={0} range={1}m cone={2}° tick={3}s diag={4}",
            _cfg.Enabled, _cfg.MaxRange, _cfg.ConeAngleDegrees, _cfg.TickSeconds, _diagLogs ? "ON" : "off"));
        ctx.Reply(string.Format("[ccddff]Filter:[-] zombies={0} animals={1} players={2}",
            _cfg.ShowZombies, _cfg.ShowAnimals, _cfg.ShowOtherPlayers));
        ctx.Reply(string.Format("[ccddff]Perm:[-] requirePerm={0} perm={1}",
            _cfg.RequirePerm, _cfg.Perm));
    }

    /// <summary>One-shot scan for the calling player — reports what (if anything)
    /// is currently being targeted, with reasons if no target. Useful for "I'm
    /// staring right at it but nothing shows" debugging.</summary>
    private void ProbePlayer(EntityPlayer p, Styx.Commands.CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World null."); return; }

        var pid = StyxCore.Player.PlatformIdOf(p);
        bool permOk = !_cfg.RequirePerm
            || (!string.IsNullOrEmpty(pid) && StyxCore.Perms.HasPermission(pid, _cfg.Perm));

        Vector3 eye = p.position + new Vector3(0f, p.GetEyeHeight(), 0f);
        Vector3 fwd = p.GetLookVector();
        ctx.Reply(string.Format("[ccddff]Probe:[-] perm={0} eye={1:F1},{2:F1},{3:F1} look={4:F2},{5:F2},{6:F2}",
            permOk, eye.x, eye.y, eye.z, fwd.x, fwd.y, fwd.z));

        if (!permOk) { ctx.Reply("[ff8888]Permission denied — not scanning.[-]"); return; }

        var target = FindAimedTarget(p, world);
        if (target == null)
        {
            // Provide context on nearby zombies so the user can see whether ANY
            // are in range / in cone / dead.
            int nearbyZombies = 0, deadZombies = 0;
            float closestZombieDist = float.PositiveInfinity;
            var list = world.Entities.list;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i] as EntityZombie;
                if (e == null) continue;
                if (e.IsDead()) { deadZombies++; continue; }
                nearbyZombies++;
                float d = Vector3.Distance(e.position, p.position);
                if (d < closestZombieDist) closestZombieDist = d;
            }
            ctx.Reply(string.Format("[ff8888]No target.[-] Nearby live zombies={0} (closest {1:F1}m), dead corpses={2}",
                nearbyZombies, closestZombieDist == float.PositiveInfinity ? -1f : closestZombieDist, deadZombies));
            ctx.Reply("Tip: aim within " + _cfg.ConeAngleDegrees + "° (very tight) and within " + _cfg.MaxRange + "m. Widen cone in config to test.");
        }
        else
        {
            int hp = Mathf.RoundToInt(target.Stats.Health.Value);
            int maxHp = Mathf.RoundToInt(target.Stats.Health.Max);
            float dist = Vector3.Distance(target.position, p.position);
            ctx.Reply(string.Format("[88ff88]Target:[-] eid={0} class={1} ({2}) hp={3}/{4} dist={5:F1}m",
                target.entityId, target.entityClass, target.GetType().Name, hp, maxHp, dist));
        }
    }

    public override void OnUnload()
    {
        _tick?.Destroy();
        _tick = null;
        StyxCore.Perms.UnregisterKnownByOwner(Name);
    }

    // ============================================================ tick

    private void Tick()
    {
        if (!_cfg.Enabled) return;

        var world = GameManager.Instance?.World;
        if (world == null) return;

        var players = StyxCore.Player?.All();
        if (players == null) return;

        foreach (var p in players)
        {
            if (p == null) continue;

            // Perm gate. We *also* zero the visible cvar for non-permitted
            // players so a player who loses the perm mid-session sees the
            // panel disappear on next tick rather than stick on stale state.
            if (_cfg.RequirePerm)
            {
                var pid = StyxCore.Player.PlatformIdOf(p);
                if (string.IsNullOrEmpty(pid) ||
                    !StyxCore.Perms.HasPermission(pid, _cfg.Perm))
                {
                    Styx.Ui.SetVar(p, "styx.zhealth.visible", 0);
                    continue;
                }
            }

            var target = FindAimedTarget(p, world);
            int playerEid = p.entityId;

            if (target == null)
            {
                Styx.Ui.SetVar(p, "styx.zhealth.visible", 0);
                if (_diagLogs)
                {
                    // Log only on TRANSITION (had a target last tick → none now).
                    if (_lastTargetByPlayer.TryGetValue(playerEid, out int prev) && prev > 0)
                    {
                        Log.Out("[StyxZombieHealth/diag] {0}: target LOST (was eid={1})",
                            p.EntityName ?? "?", prev);
                        _lastTargetByPlayer[playerEid] = 0;
                    }
                }
                continue;
            }

            int hp      = Mathf.Max(0, Mathf.RoundToInt(target.Stats.Health.Value));
            int maxHp   = Mathf.Max(1, Mathf.RoundToInt(target.Stats.Health.Max));
            int pct     = Mathf.Clamp((hp * 100) / maxHp, 0, 100);
            // Look up the small sequential label index for this entity class.
            // Falls back to 0 (which maps to "Target" via the fallback label
            // registered below) if we don't have a registration for this
            // class — handles edge cases like modlet entities added after
            // server boot.
            int labelIdx;
            if (!_classIdToLabelIdx.TryGetValue(target.entityClass, out labelIdx))
                labelIdx = 0;

            Styx.Ui.SetVar(p, "styx.zhealth.visible", 1);
            Styx.Ui.SetVar(p, "styx.zhealth.classid", labelIdx);
            Styx.Ui.SetVar(p, "styx.zhealth.hp_curr", hp);
            Styx.Ui.SetVar(p, "styx.zhealth.hp_max", maxHp);
            Styx.Ui.SetVar(p, "styx.zhealth.hp_pct", pct);

            if (_diagLogs)
            {
                // Log only on TRANSITION (no target last tick or different target).
                if (!_lastTargetByPlayer.TryGetValue(playerEid, out int prev) || prev != target.entityId)
                {
                    Log.Out("[StyxZombieHealth/diag] {0}: target ACQUIRED eid={1} class={2} hp={3}/{4}",
                        p.EntityName ?? "?", target.entityId, target.entityClass, hp, maxHp);
                    _lastTargetByPlayer[playerEid] = target.entityId;
                }
            }
        }
    }

    // ============================================================ targeting

    /// <summary>Iterate world entities, return the closest valid candidate
    /// the player's look-ray actually intersects. Two-stage filter:
    ///   1. Cheap cone pre-filter (drops entities clearly off-axis)
    ///   2. Ray-vs-bounding-sphere test (accurate "is the crosshair on the body?")
    ///
    /// The cone is just an early-out — the ray-vs-sphere is the final arbiter.
    /// At point-blank range the cone alone fails (a zombie torso 0.9m below
    /// eye level at 1m distance is at 40°+ from a forward look-vec), but the
    /// ray-vs-sphere correctly registers because the look-ray passes within
    /// EntityHitRadius of the torso.</summary>
    private EntityAlive FindAimedTarget(EntityPlayer player, World world)
    {
        Vector3 eye = player.position + new Vector3(0f, player.GetEyeHeight(), 0f);
        Vector3 fwd = player.GetLookVector();
        if (fwd.sqrMagnitude < 0.0001f) return null;

        // Recompute cone threshold per-tick so config hot-reload picks up changes.
        float coneCos = Mathf.Cos(_cfg.ConeAngleDegrees * Mathf.Deg2Rad);
        float radius  = _cfg.EntityHitRadius;
        float maxRng  = _cfg.MaxRange;

        EntityAlive best = null;
        float bestAlong = maxRng + 1f;  // along-axis distance for "closer = wins"

        var list = world.Entities.list;
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i] as EntityAlive;
            if (e == null || e == player) continue;
            if (e.IsDead()) continue;

            // Type filter
            if (e is EntityZombie)        { if (!_cfg.ShowZombies)       continue; }
            else if (e is EntityAnimal)   { if (!_cfg.ShowAnimals)       continue; }
            else if (e is EntityPlayer)   { if (!_cfg.ShowOtherPlayers)  continue; }
            else                          { continue; }

            // Cheap distance cull on entity center.
            Vector3 center = e.position + new Vector3(0f, 0.9f, 0f);
            Vector3 toCenter = center - eye;
            float centerDistSq = toCenter.sqrMagnitude;
            if (centerDistSq < 0.01f)
            {
                // Player standing inside entity — count as overlapping hit.
                if (0f < bestAlong) { best = e; bestAlong = 0f; }
                continue;
            }
            float centerDist = Mathf.Sqrt(centerDistSq);

            // Cone pre-filter — only used at distance, where it's a useful
            // early-out. At close range (<5m) we skip it because point-blank
            // angles to specific body parts can be steep even when the
            // player is clearly aimed at the body. The multi-sample sphere
            // test below is the actual hit decider.
            if (centerDist > 5f)
            {
                float dot = Vector3.Dot(fwd, toCenter) / centerDist;
                if (dot < coneCos) continue;
            }

            // Multi-sample ray-vs-sphere. Test 3 points up the entity's
            // vertical axis: hips (0.4m), chest (1.0m), head (1.6m) above
            // feet. Hit if the look-ray passes within `radius` of ANY
            // sample. Approximates a vertical capsule body collider —
            // catches head shots, body shots, and leg shots equally.
            float entityBestAlong = float.MaxValue;
            bool entityHit = false;

            for (int s = 0; s < _sampleHeights.Length; s++)
            {
                Vector3 samplePoint = e.position + new Vector3(0f, _sampleHeights[s], 0f);
                Vector3 toSample = samplePoint - eye;
                float along = Vector3.Dot(toSample, fwd);

                if (along < 0f) continue;        // sample behind player
                if (along > maxRng) continue;    // sample out of range

                Vector3 closestPointOnRay = eye + fwd * along;
                float perpDist = (samplePoint - closestPointOnRay).magnitude;
                if (perpDist > radius) continue; // ray misses this sample's sphere

                // Hit on this sample. Track the smallest along (closest
                // point on body to player) so distance-based picking
                // between entities is consistent.
                if (along < entityBestAlong) entityBestAlong = along;
                entityHit = true;
            }

            if (!entityHit) continue;
            if (entityBestAlong >= bestAlong) continue;  // already have a closer entity

            best = e;
            bestAlong = entityBestAlong;
        }

        return best;
    }

    /// <summary>Vertical sample heights (metres above feet) used by the
    /// multi-sample ray-vs-sphere test in <see cref="FindAimedTarget"/>.
    /// 0.4 = hips, 1.0 = chest, 1.6 = head. Together with the 0.7m hit
    /// radius these cover a humanoid capsule including arms.</summary>
    private static readonly float[] _sampleHeights = { 0.4f, 1.0f, 1.6f };

    // ============================================================ name labels

    /// <summary>Walk EntityClass.list and register a localization label per
    /// class id, mapping the int cvar styx.zhealth.classid → display name.
    /// Called once at OnLoad. Labels persist via Styx.Ui.Labels which writes
    /// them to <c>Mods/StyxRuntime/Config/Localization.txt</c> at server
    /// shutdown — engine reads that file on the NEXT boot's init phase, so
    /// names appear after the second restart following first deploy.
    ///
    /// Registers labels for EVERY entity class (zombies / animals / players /
    /// items-as-entities / etc.) — wasteful but cheap (~500 entries × 30
    /// chars). Avoids per-tick type checks AND covers any zombie added by a
    /// modlet without code changes.</summary>
    private int RegisterEntityNameLabels()
    {
        int n = 0;
        try
        {
            _classIdToLabelIdx.Clear();

            // Fallback for index=0 / unknown — XUi shows this when the cvar
            // is unset OR the entity class wasn't in our scan (e.g. added
            // by a modlet after boot).
            Styx.Ui.Labels.Register(this, "styx_zh_e_0", "Target");

            // Walk every EntityClass and assign small sequential label indices.
            // Sort by classId for deterministic index assignment (so the same
            // server config produces the same indices across restarts — keeps
            // the localization file diff-stable).
            var sortedIds = new System.Collections.Generic.List<int>(EntityClass.list.Dict.Keys);
            sortedIds.Sort();

            int nextIdx = 1;
            foreach (int classId in sortedIds)
            {
                var ec = EntityClass.list.Dict[classId];
                if (ec == null) continue;
                string raw = ec.entityClassName;
                if (string.IsNullOrEmpty(raw)) continue;

                _classIdToLabelIdx[classId] = nextIdx;
                Styx.Ui.Labels.Register(this, "styx_zh_e_" + nextIdx, PrettifyClassName(raw));
                nextIdx++;
                n++;
            }
        }
        catch (Exception e)
        {
            Log.Warning("[StyxZombieHealth] RegisterEntityNameLabels failed: " + e.Message);
        }
        return n;
    }

    /// <summary>"zombieFeralWight" → "Feral Wight". "animalDeer" → "Deer".
    /// Strips well-known prefixes (zombie / animal) and inserts spaces before
    /// internal capital letters. Title-cases the first letter. Good enough
    /// without doing a real localization-key lookup per class.</summary>
    private string PrettifyClassName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;

        string s = raw;
        // Strip common prefixes — most zombie classes start with "zombie",
        // most animals with "animal".
        if (s.StartsWith("zombie", StringComparison.OrdinalIgnoreCase) && s.Length > 6)
            s = s.Substring(6);
        else if (s.StartsWith("animal", StringComparison.OrdinalIgnoreCase) && s.Length > 6)
            s = s.Substring(6);

        // Insert spaces before internal capitals: "FeralWight" → "Feral Wight"
        var sb = new System.Text.StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1]))
                sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpper(s[i]) : s[i]);
        }
        return sb.ToString();
    }
}
