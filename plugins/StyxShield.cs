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

// StyxShield v0.3.0 -- "force-field" / sanctuary buff bound to land claims.
//
// While shield is active on YOUR LCB and you stand inside it:
//   - Zombies don't notice you (sight or sound aggro is filtered)
//   - Noise events originating inside the LCB are filtered (rocks, gunfire,
//     screamer calls -- screamer can't summon a horde without aggro)
//   - Idle wander destinations inside the LCB are rejected (zombies that
//     drift past will steer around)
//   - Bloodmoon: protection auto-suspends. Slot stays used; coverage
//     resumes after blood moon ends.
//
// Mechanics live in framework: Styx.Shield static registry +
// Styx.Hooks.FirstParty.ShieldGuard Harmony patches. This plugin owns
// the activate/deactivate UX, persistence, and per-player slot accounting.
//
// Toggle from inside YOUR own LCB:
//   /shield      -- toggle shield on the LCB you're standing in
//   /m -> Shield -- opens the info+toggle UI; LMB toggles, RMB closes
//
// Perms:
//   styx.shield.use   -- show /m -> Shield + run /shield
//
// Persistence:
//   data/StyxShield/zones.json -- {ownerPid: [LCB centers]}.

using System;
using System.Collections.Generic;
using Styx;
using Styx.Data;
using Styx.Plugins;
using Styx.Scheduling;


/* @styx-xui-windows
<!--
    styxShield — Sanctuary toggle UI (StyxShield.cs).

    Single-page panel. State driven by:
      styx.shield.open         panel visible
      styx.shield.active       1 if player owns &gt;= 1 active shield
      styx.shield.slots_used   current slot count
      styx.shield.slots_max    config cap
      styx.shield.bloodmoon    1 if protection currently suspended
      styx.shield.here         1 if player standing in their own LCB
      styx.shield.here_active  1 if THIS LCB is the shielded one

    Inputs: LMB toggle current LCB, RMB back to launcher.
-->
<window name="styxShield"
        anchor="CenterCenter" pos="-260,170"
        width="520" height="340"
        pivot="TopLeft"
        controller="ToolbeltWindow"
        depth="52">

    <rect name="wrap" pos="0,0" width="520" height="340"
          visible="{#cvar('styx.shield.open') == 1}">

        <sprite depth="0" name="bg"     sprite="menu_empty"    color="0,0,0,210"        type="sliced" width="520" height="340" />
        <sprite depth="1" name="border" sprite="menu_empty3px" color="120,200,255,220"  type="sliced" width="520" height="340" fillcenter="false" />

        <label depth="2" name="hdr" text="STYX SHIELD"
               font_size="26" justify="center" style="outline"
               color="120,200,255,255"
               pos="260,-10" width="520" height="30" pivot="top" />

        <!-- ===== Status row: ACTIVE / INACTIVE ===== -->
        <label depth="3" name="stActive" text="ACTIVE"
               font_size="22" justify="center" style="outline"
               pos="260,-50" width="520" height="28" pivot="top"
               color="100,255,140,255"
               visible="{#cvar('styx.shield.active') == 1}" />
        <label depth="3" name="stInactive" text="INACTIVE"
               font_size="22" justify="center" style="outline"
               pos="260,-50" width="520" height="28" pivot="top"
               color="180,180,180,255"
               visible="{#cvar('styx.shield.active') == 0}" />

        <!-- ===== Bloodmoon suspension banner ===== -->
        <label depth="3" name="bmBanner"
               text="SUSPENDED — Blood Moon"
               font_size="16" justify="center" style="outline"
               pos="260,-80" width="520" height="22" pivot="top"
               color="255,120,120,255"
               visible="{#cvar('styx.shield.bloodmoon') == 1}" />

        <!-- ===== Slot usage ===== -->
        <label depth="3" name="slots"
               text="Slots used: {cvar(styx.shield.slots_used:0)} / {cvar(styx.shield.slots_max:0)}"
               font_size="16" justify="center"
               pos="260,-108" width="520" height="22" pivot="top"
               color="200,220,255,255" />

        <!-- ===== Description ===== -->
        <label depth="3" name="d1"
               text="Zombies don't notice you while inside your land claim."
               font_size="15" justify="center"
               pos="260,-142" width="500" height="22" pivot="top"
               color="220,220,220,255" />
        <label depth="3" name="d2"
               text="Walls of stealth: aggro, noise, and idle wander all filtered."
               font_size="15" justify="center"
               pos="260,-166" width="500" height="22" pivot="top"
               color="220,220,220,255" />
        <label depth="3" name="d3"
               text="Stand inside YOUR land claim to toggle. Suspends during blood moon."
               font_size="15" justify="center"
               pos="260,-190" width="500" height="22" pivot="top"
               color="180,200,220,255" />

        <!-- ===== Toggle prompt: depends on (here, here_active) ===== -->
        <!-- (a) Not standing in own LCB -->
        <label depth="3" name="needLcb"
               text="STAND IN YOUR LCB TO TOGGLE"
               font_size="18" justify="center" style="outline"
               pos="260,-230" width="520" height="26" pivot="top"
               color="255,180,120,255"
               visible="{#cvar('styx.shield.here') == 0}" />
        <!-- (b) In own LCB, this one is shielded -->
        <label depth="3" name="canDeact"
               text="[LMB] DEACTIVATE THIS CLAIM"
               font_size="18" justify="center" style="outline"
               pos="260,-230" width="520" height="26" pivot="top"
               color="255,200,120,255"
               visible="{#(cvar('styx.shield.here') == 1) and (cvar('styx.shield.here_active') == 1)}" />
        <!-- (c) In own LCB, this one not shielded -->
        <label depth="3" name="canAct"
               text="[LMB] ACTIVATE THIS CLAIM"
               font_size="18" justify="center" style="outline"
               pos="260,-230" width="520" height="26" pivot="top"
               color="120,255,180,255"
               visible="{#(cvar('styx.shield.here') == 1) and (cvar('styx.shield.here_active') == 0)}" />

        <!-- Legend -->
        <label depth="3" name="legend"
               text="[LMB] toggle   [RMB] back"
               font_size="16" justify="center"
               pos="260,-310" width="520" height="22" pivot="top"
               color="180,180,180,255" />
    </rect>
</window>
*/

/* @styx-xui-window-group toolbelt
<window name="styxShield" />
*/

/* @styx-buffs
<!--
    Visual marker for an active StyxShield. No mechanical effect —
    the actual stealth/repulsion is delivered by the framework's
    Harmony patches (Styx.Hooks.FirstParty.ShieldGuard) reading from
    Styx.Shield.IsPlayerShielded. The buff just gives the player an
    icon they can see in their HUD so they know the shield is on.

    Long duration; the plugin re-applies on join/spawn and on
    activate. Removed by plugin on /shield off or LCB destruction.
-->
<buff name="buffStyxShieldActive"
      name_key="buffStyxShieldActiveName"
      description_key="buffStyxShieldActiveDesc"
      icon="ui_game_symbol_defense">
    <stack_type value="replace"/>
    <duration value="999999"/>
</buff>
*/

[Info("StyxShield", "Doowkcol", "0.3.0")]
public class StyxShield : StyxPlugin
{
    public override string Description => "Stealth + soft-repulsion zombie shield bound to land claims";

    private const string PermUse        = "styx.shield.use";
    private const string ShieldBuffName = "buffStyxShieldActive";

    // ---- UI cvars (Recipe B / Ephemeral) ----
    private const string CvOpen     = "styx.shield.open";
    private const string CvActive   = "styx.shield.active";       // 1 if any shield owned
    private const string CvSlots    = "styx.shield.slots_used";
    private const string CvSlotsMax = "styx.shield.slots_max";
    private const string CvBmSusp   = "styx.shield.bloodmoon";    // 1 if currently suspended
    private const string CvHere     = "styx.shield.here";         // 1 if player standing inside an OWN LCB
    private const string CvHereOn   = "styx.shield.here_active";  // 1 if THIS LCB is the shielded one

    // Players currently in the UI -- tracked separately from CvOpen so we
    // can release input claims cleanly on plugin unload.
    private readonly System.Collections.Generic.HashSet<int> _uiOpenFor =
        new System.Collections.Generic.HashSet<int>();

    public class Config
    {
        // Cap on simultaneous active shields per player. Default 1 means
        // operators can place multiple LCBs but only sanctify one at a time.
        public int MaxActivePerPlayer = 1;

        // When true (default), Shield.IsBloodmoonGate honors the world's
        // BloodMoon event -- protection silently suspends. Set false if your
        // server wants the shield to also block during blood moon (rarely
        // sensible since the whole point of bloodmoon is to face the horde).
        public bool BlockOnBloodmoon = true;

        // When true, normal animals (bears, wolves, mountain lions etc.)
        // are also filtered. Default false -- only undead are blocked, so
        // wildlife threat remains inside shielded zones. Zombie dogs and
        // other undead-tagged animals are ALWAYS blocked regardless of
        // this flag.
        public bool BlockRegularAnimals = false;

        // When true, hostile bandit NPCs are also filtered. Default false
        // -- bandits are a distinct threat class.
        public bool BlockBandits = false;

        // Dirty-batch flush interval for the zones file.
        public int FlushIntervalSeconds = 30;

        // Verbose log lines on activate/deactivate. Useful while shaking
        // out behavior; turn off once stable.
        public bool Verbose = true;
    }

    /// <summary>
    /// Per-owner persisted state. Keyed by platform-id-combined string;
    /// value is the list of LCB centers the owner currently has shielded.
    /// </summary>
    public class State
    {
        public Dictionary<string, List<Vector3i>> Active =
            new Dictionary<string, List<Vector3i>>(StringComparer.OrdinalIgnoreCase);
    }

    private Config _cfg;
    private DataStore<State> _state;
    private TimerHandle _flushTick;
    private bool _dirty;

    public override void OnLoad()
    {
        _cfg   = StyxCore.Configs.Load<Config>(this);
        _state = this.Data.Store<State>("zones");

        StyxCore.Perms.RegisterKnown(PermUse,
            "Toggle a sanctuary shield on your own LCB", Name);

        // Wire bloodmoon gate. When the operator wants protection to extend
        // through bloodmoon, override the gate to a constant-false.
        if (!_cfg.BlockOnBloodmoon)
            Shield.IsBloodmoonGate = () => false;

        // Wire opt-in threat-class flags. Default false: undead only.
        Shield.BlockRegularAnimals = _cfg.BlockRegularAnimals;
        Shield.BlockBandits        = _cfg.BlockBandits;

        // Single-purpose chat command: bare /shield toggles. The UI carries
        // the discoverability and status display.
        StyxCore.Commands.Register("shield",
            "Toggle a shield on the LCB you're standing in -- /shield",
            CmdShield);

        // Register UI cvars as ephemeral so the panel doesn't resurrect
        // itself across server restart.
        Styx.Ui.Ephemeral.Register(CvOpen, CvActive, CvSlots, CvSlotsMax,
            CvBmSusp, CvHere, CvHereOn);

        Styx.Ui.Menu.Register(this,
            "Shield  /shield",
            OpenUi,
            permission: PermUse);

        // Hydrate the framework registry from disk.
        int half = ResolveHalfSize();
        int restored = 0;
        foreach (var kv in _state.Value.Active)
        {
            foreach (var center in kv.Value)
            {
                Shield.Activate(new Shield.Zone
                {
                    Center        = center,
                    Half          = half,
                    OwnerPid      = kv.Key,
                    OwnerEntityId = -1,
                });
                restored++;
            }
        }

        int flushSecs = Math.Max(5, _cfg.FlushIntervalSeconds);
        _flushTick = Scheduler.Every(flushSecs, FlushIfDirty, name: "StyxShield.flush");

        Log.Out("[StyxShield] Loaded v0.3.0 -- {0} shield(s) restored, max-per-player={1}, bloodmoon-suspends={2}, animals={3}, bandits={4}",
            restored, _cfg.MaxActivePerPlayer, _cfg.BlockOnBloodmoon,
            _cfg.BlockRegularAnimals, _cfg.BlockBandits);
    }

    public override void OnUnload()
    {
        _flushTick?.Destroy();
        _flushTick = null;
        FlushIfDirty();
        Shield.Clear();
        // Reset bloodmoon gate so a future plugin reload gets the framework default.
        Shield.IsBloodmoonGate = null;
        // Reset opt-in threat flags so they don't carry stale values into
        // a hot-reload that uses a different config.
        Shield.BlockRegularAnimals = false;
        Shield.BlockBandits        = false;
        StyxCore.Perms.UnregisterKnownByOwner(Name);
        Styx.Ui.Menu.UnregisterAll(this);

        // Release every UI input claim we still hold.
        foreach (var eid in _uiOpenFor)
        {
            var p = StyxCore.Player.FindByEntityId(eid);
            if (p == null) continue;
            Styx.Ui.SetVar(p, CvOpen, 0f);
            Styx.Ui.Input.Release(p, Name);
        }
        _uiOpenFor.Clear();
    }

    private void FlushIfDirty()
    {
        if (!_dirty) return;
        _state.Save();
        _dirty = false;
    }

    // ============================================================ Hook handlers

    /// <summary>
    /// Re-resolve OwnerEntityId for any zones owned by this player and
    /// re-apply the visible Sanctuary buff if they own at least one zone.
    /// Hook bus contract: name MUST be OnPlayerSpawned with the
    /// (ClientInfo, RespawnType, Vector3i) signature -- the previous
    /// "OnPlayerSpawnedInWorld(EntityPlayer)" form is silently ignored
    /// by the bus and the handler never fires.
    /// </summary>
    void OnPlayerSpawned(ClientInfo client, RespawnType reason, Vector3i pos)
    {
        if (client == null) return;
        var pid = client.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid)) return;

        Shield.SetOwnerEntityId(pid, client.entityId);

        // Re-apply visible buff if this player owns active zones.
        if (_state.Value.Active.TryGetValue(pid, out var list) && list.Count > 0)
        {
            var p = StyxCore.Player.FindByEntityId(client.entityId);
            if (p != null) ApplyBuff(p);
        }
    }

    // ============================================================ Commands

    /// <summary>
    /// Bare /shield -- toggle the LCB you're standing in. No subcommands;
    /// the UI (/m -> Shield) carries discoverability and status display.
    /// </summary>
    private void CmdShield(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }
        var pid = ctx.Client.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid)) { ctx.Reply("[ff6666]Could not resolve player id.[-]"); return; }
        if (!StyxCore.Perms.HasPermission(pid, PermUse))
        { ctx.Reply("[ff6666]No permission '" + PermUse + "'.[-]"); return; }

        Toggle(ctx.Client, msg => ctx.Reply(msg));
    }

    // ============================================================ Toggle / activate / deactivate

    /// <summary>
    /// Activate the LCB the player is standing inside, OR deactivate it if
    /// already active.
    /// </summary>
    private void Toggle(ClientInfo client, Action<string> reply)
    {
        if (!ResolveStandingLcb(client, reply, out var pid, out var lcb)) return;

        var owned = GetOwnedZonesList(pid);
        bool alreadyActive = owned.Contains(lcb);
        if (alreadyActive) DoDeactivate(client, pid, lcb, reply);
        else               DoActivate(client, pid, lcb, reply);
    }

    private void DoActivate(ClientInfo client, string pid, Vector3i lcb, Action<string> reply)
    {
        var owned = GetOwnedZonesList(pid);
        if (owned.Count >= _cfg.MaxActivePerPlayer)
        {
            reply(string.Format(
                "[ff6666][Shield] You've hit your shield limit ({0}). Deactivate one first.[-]",
                _cfg.MaxActivePerPlayer));
            return;
        }

        owned.Add(lcb);
        _dirty = true;
        Shield.Activate(new Shield.Zone
        {
            Center        = lcb,
            Half          = ResolveHalfSize(),
            OwnerPid      = pid,
            OwnerEntityId = client.entityId,
        });

        var p = StyxCore.Player.FindByEntityId(client.entityId);
        if (p != null) ApplyBuff(p);

        if (_cfg.Verbose)
            Log.Out("[StyxShield] {0} activated shield @ ({1}) [{2}/{3}]",
                client.playerName, lcb, owned.Count, _cfg.MaxActivePerPlayer);

        reply(string.Format(
            "[00ff66][Shield] Shield active on this claim. {0}/{1} slots used.[-]",
            owned.Count, _cfg.MaxActivePerPlayer));
        if (_cfg.BlockOnBloodmoon)
            reply("[ffaa00]Note: protection suspends during blood moon.[-]");

        // If this player has the UI open, refresh its state so the toggle
        // result is reflected without closing the panel.
        if (p != null && _uiOpenFor.Contains(p.entityId)) RefreshUiState(p);
    }

    private void DoDeactivate(ClientInfo client, string pid, Vector3i lcb, Action<string> reply)
    {
        var owned = GetOwnedZonesList(pid);
        owned.Remove(lcb);
        if (owned.Count == 0) _state.Value.Active.Remove(pid);
        _dirty = true;
        Shield.Deactivate(pid, lcb);

        var p = StyxCore.Player.FindByEntityId(client.entityId);

        // Strip visible buff only if no other shield remains for the player.
        if (owned.Count == 0 && p != null) RemoveBuff(p);

        if (_cfg.Verbose)
            Log.Out("[StyxShield] {0} deactivated shield @ ({1}) [{2}/{3}]",
                client.playerName, lcb, owned.Count, _cfg.MaxActivePerPlayer);

        reply(string.Format(
            "[ffaa00][Shield] Shield off for this claim. {0}/{1} slots used.[-]",
            owned.Count, _cfg.MaxActivePerPlayer));

        if (p != null && _uiOpenFor.Contains(p.entityId)) RefreshUiState(p);
    }

    // ============================================================ UI lifecycle

    /// <summary>
    /// Open the Shield panel. Snapshot state into cvars for visibility
    /// bindings, claim input, drop the player into the open set.
    /// </summary>
    private void OpenUi(EntityPlayer p)
    {
        if (p == null) return;
        Styx.Ui.SetVar(p, CvOpen, 1f);
        RefreshUiState(p);
        Styx.Ui.Input.Acquire(p, Name);
        _uiOpenFor.Add(p.entityId);
    }

    private void CloseUi(EntityPlayer p)
    {
        if (p == null) return;
        Styx.Ui.SetVar(p, CvOpen, 0f);
        Styx.Ui.Input.Release(p, Name);
        _uiOpenFor.Remove(p.entityId);
    }

    /// <summary>
    /// Re-populate UI cvars based on the player's current state. Called on
    /// open, after every toggle, and (TODO) optionally on bloodmoon edges.
    /// </summary>
    private void RefreshUiState(EntityPlayer p)
    {
        if (p == null) return;
        var ci = StyxCore.Player.ClientOf(p);
        var pid = ci?.PlatformId?.CombinedString ?? "";

        var owned = GetOwnedZonesList(pid);
        bool bmSusp = Shield.IsBloodmoonGate?.Invoke() ?? false;
        Styx.Ui.SetVar(p, CvActive,   owned.Count > 0 ? 1 : 0);
        Styx.Ui.SetVar(p, CvSlots,    owned.Count);
        Styx.Ui.SetVar(p, CvSlotsMax, _cfg.MaxActivePerPlayer);
        Styx.Ui.SetVar(p, CvBmSusp,   bmSusp ? 1 : 0);

        // Eligibility: am I standing in my own LCB right now?
        bool here = false;
        bool hereOn = false;
        if (!string.IsNullOrEmpty(pid))
        {
            var lcbs = FindPlayerLCBs(pid);
            if (lcbs.Count > 0)
            {
                int half = ResolveHalfSize();
                var pos = new Vector3i(
                    (int)Math.Floor(p.position.x),
                    (int)Math.Floor(p.position.y),
                    (int)Math.Floor(p.position.z));
                var lcb = FindContainingLCB(pos, lcbs, half);
                if (lcb.HasValue)
                {
                    here = true;
                    hereOn = owned.Contains(lcb.Value);
                }
            }
        }
        Styx.Ui.SetVar(p, CvHere,   here   ? 1 : 0);
        Styx.Ui.SetVar(p, CvHereOn, hereOn ? 1 : 0);
    }

    /// <summary>
    /// Auto-subscribed input handler. LMB = toggle current LCB (refreshes
    /// state, panel stays open). RMB = back to launcher. Scroll unused
    /// (single-page panel, nothing to navigate).
    /// </summary>
    void OnPlayerInput(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (p?.Buffs == null) return;
        if ((int)p.Buffs.GetCustomVar(CvOpen) != 1) return;

        switch (kind)
        {
            case Styx.Ui.StyxInputKind.PrimaryAction:
            {
                var ci = StyxCore.Player.ClientOf(p);
                if (ci == null) { Styx.Server.Whisper(p, "[ff6666][Shield] Client not found.[-]"); return; }
                Toggle(ci, msg => Styx.Server.Whisper(p, msg));
                // Toggle's success path already calls RefreshUiState; the
                // failure paths (not-in-LCB etc.) only whisper, so cvars
                // stay correct. Refresh defensively in case the player has
                // moved since OpenUi captured them.
                RefreshUiState(p);
                break;
            }
            case Styx.Ui.StyxInputKind.SecondaryAction:
                CloseUi(p);
                Styx.Scheduling.Scheduler.Once(0.05,
                    () => Styx.Ui.Menu.OpenLauncher?.Invoke(p),
                    name: "StyxShield.BackToLauncher");
                break;
        }
    }

    // ============================================================ Helpers

    private List<Vector3i> GetOwnedZonesList(string pid)
    {
        if (!_state.Value.Active.TryGetValue(pid, out var list))
        {
            list = new List<Vector3i>();
            _state.Value.Active[pid] = list;
        }
        return list;
    }

    /// <summary>
    /// Resolve "the LCB this player is standing inside, owned by them".
    /// Whispers the appropriate error and returns false on any failure.
    /// </summary>
    private bool ResolveStandingLcb(ClientInfo client, Action<string> reply,
                                    out string pid, out Vector3i lcb)
    {
        pid = "";
        lcb = default;

        var p = StyxCore.Player.FindByEntityId(client.entityId);
        if (p == null) { reply("[ff6666]Player not found.[-]"); return false; }
        pid = client.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid)) { reply("[ff6666]Could not resolve player id.[-]"); return false; }

        var lcbs = FindPlayerLCBs(pid);
        if (lcbs.Count == 0)
        {
            reply("[ff6666][Shield] You don't own any land claim.[-]");
            return false;
        }

        int half = ResolveHalfSize();
        var pos = new Vector3i(
            (int)Math.Floor(p.position.x),
            (int)Math.Floor(p.position.y),
            (int)Math.Floor(p.position.z));
        var contains = FindContainingLCB(pos, lcbs, half);
        if (!contains.HasValue)
        {
            reply("[ff6666][Shield] You must be standing inside one of your own land claims.[-]");
            return false;
        }
        lcb = contains.Value;
        return true;
    }

    private static int ResolveHalfSize()
    {
        int radius = GameStats.GetInt(EnumGameStats.LandClaimSize);
        if (radius <= 0) radius = 41;
        return (radius - 1) / 2;
    }

    private static List<Vector3i> FindPlayerLCBs(string playerId)
    {
        var result = new List<Vector3i>();
        var ppl = GameManager.Instance?.GetPersistentPlayerList();
        if (ppl == null) return result;
        foreach (var kv in ppl.Players)
        {
            var ppd = kv.Value;
            if (ppd?.LPBlocks == null || ppd.LPBlocks.Count == 0) continue;
            // Match against PrimaryId or NativeId -- crossplay quirk where
            // PPD.PrimaryId can be EOS_xxx while ClientInfo.PlatformId is
            // Steam_xxx. Same logic StyxBuilder uses.
            bool hit =
                string.Equals(ppd.PrimaryId?.CombinedString, playerId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ppd.NativeId?.CombinedString,  playerId, StringComparison.OrdinalIgnoreCase);
            if (!hit) continue;
            foreach (var lcb in ppd.LPBlocks) result.Add(lcb);
        }
        return result;
    }

    private static Vector3i? FindContainingLCB(Vector3i playerPos, List<Vector3i> lcbs, int half)
    {
        foreach (var lcb in lcbs)
        {
            if (Math.Abs(playerPos.x - lcb.x) <= half &&
                Math.Abs(playerPos.z - lcb.z) <= half) return lcb;
        }
        return null;
    }

    private void ApplyBuff(EntityPlayer p)
    {
        try { StyxCore.Player.ApplyBuff(p, ShieldBuffName, duration: 999999); }
        catch (Exception e) { Log.Warning("[StyxShield] ApplyBuff threw: " + e.Message); }
    }

    private void RemoveBuff(EntityPlayer p)
    {
        try { p.Buffs?.RemoveBuff(ShieldBuffName); }
        catch (Exception e) { Log.Warning("[StyxShield] RemoveBuff threw: " + e.Message); }
    }
}
