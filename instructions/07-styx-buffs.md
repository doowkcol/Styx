# 07 — StyxBuffs

Defines a flat list of buffs (and optional CVar writes), each gated by a permission. The picker UI shows **every configured buff to every player** -- even ones they don't have a gate-perm for, marked `[No Perm]`, so they can see what's possible. Two flavours:

- **Toggle buffs** (`CooldownSeconds = 0`) -- always-on while perm + ON. Auto-apply on join/spawn + reapply tick. Player toggles via LMB.
- **Cooldown buffs** (`CooldownSeconds > 0`) -- on-demand. LMB on a Ready row applies the buff for `DurationSeconds`, then enters cooldown for `CooldownSeconds`, then becomes Ready again. Never auto-applied.

Multiple buffs can share a perm; perm-to-group assignment lives in the standard perm editor (`/perm` or `/m → Perm Editor`).

**Renamed from DonorPerks (v0.3.x → v0.4.0):** group-keyed config (`GroupBuffs[group] -> [buffs]`) is gone; identity moved from `DonorPerks` / `styx.donor.*` to `StyxBuffs` / `styx.buffs.*`. Old `configs/DonorPerks.json` files don't auto-load -- rename to `configs/StyxBuffs.json` first; legacy `GroupBuffs` keys then auto-migrate to the new shape on first load.

## Commands

| Command | Perm | What |
|---|---|---|
| `/buffs` | `styx.buffs.use` | Open the My Buffs picker UI (same as `/m → My Buffs`) |
| `/buffs close` | `styx.buffs.use` | Force-close the panel |
| `/buffs status` | (open) | Chat dump of every configured buff + your status (No Perm / On / Off / Ready / Active / Cooldown) |
| `/buffs reapply` | `styx.buffs.admin` | Force re-apply for everyone now (debugging) |
| `/m → My Buffs` | `styx.buffs.use` | Picker UI to toggle your own buffs ON/OFF |

## Permissions

The plugin auto-registers two framework-baseline perms plus every distinct gate-perm declared on a buff/cvar entry, so they all show up in the perm editor.

| Perm | What |
|---|---|
| `styx.buffs.use` | Required to open the `/m → My Buffs` toggle UI |
| `styx.buffs.admin` | Force-reapply for all players, view everyone's perk state |
| `styx.buffs.vip` (default) | Gates the bundled VIP buff set |
| `styx.buffs.<custom>` | Any perm name an operator types into a buff/cvar entry |

To grant a tier of buffs to a group, open the perm editor and tick the gate-perm on the group: `/perm` → pick group → pick `StyxBuffs` → toggle on.

## Config — `configs/StyxBuffs.json`

```json
{
  "ReapplyIntervalSeconds": 1800,
  "Buffs": [
    {
      "Buff": "buffStyxVipUndead",
      "Perm": "styx.buffs.vip",
      "DurationSeconds": 3600,
      "CooldownSeconds": 0,
      "DisplayName": "VIP: Undead Slayer",
      "Description": "+100% damage to zombies",
      "UserToggleable": true,
      "Icon": "ui_game_symbol_zombie"
    },
    {
      "Buff": "buffMegaCrush",
      "Perm": "styx.buffs.vip",
      "DurationSeconds": 600,
      "CooldownSeconds": 3600,
      "DisplayName": "Mega Crush (10m)",
      "Description": "10 min stamina + damage rush, 1h cooldown",
      "UserToggleable": true,
      "Icon": "ui_game_symbol_fist"
    }
  ],
  "CVars": [
    { "Name": "$treatedCommando", "Value": 0.5, "Perm": "styx.buffs.vip" }
  ]
}
```

| Field | Meaning |
|---|---|
| `ReapplyIntervalSeconds` | How often the periodic re-apply sweep runs (default 30 min) |
| `Buffs[].Buff` | Buff name (must be defined in vanilla or a modlet) |
| `Buffs[].Perm` | Gate -- player must hold this perm. Empty = entry skipped |
| `Buffs[].DurationSeconds` | Buff duration when applied; default 3600 |
| `Buffs[].CooldownSeconds` | `0` = toggle buff (always-on while ON). `> 0` = on-demand cooldown buff (LMB on Ready, runs for `DurationSeconds`, locked out for `CooldownSeconds` after) |
| `Buffs[].DisplayName` | Optional UI label; falls back to `Buff` if null/empty |
| `Buffs[].Description` | Description line shown below the row list and in whispers |
| `Buffs[].UserToggleable` | False = player can't toggle / activate this entry |
| `Buffs[].Icon` | Optional UIAtlas sprite name |
| `CVars[].Name` | Entity CVar to set (e.g. `$treatedCommando`) |
| `CVars[].Value` | Float value to write |
| `CVars[].Perm` | Gate -- player must hold this perm |

### Multiple entries per buff

Two entries with the same `Buff` but different `Perm` is fine -- the player gets the buff if they hold _any_ of the gate-perms. The first matching entry's metadata (DisplayName, Icon, etc.) wins. Useful when you want the same buff granted via a tier perm and via a holiday/event perm without coupling them.

## Mechanics

### Per-row status (what the player sees)

| Status | Meaning | LMB action |
|---|---|---|
| `[No Perm]` | Player doesn't hold the gate-perm | whisper "no perm" |
| `OFF` | Toggle buff, perm OK, user disabled | toggle ON |
| `ON` | Toggle buff, perm OK, active | toggle OFF |
| `Ready` | Cooldown buff, perm OK, available | activate (apply for DurationSeconds) |
| `Active Xs` | Cooldown buff currently buffed | whisper time-left, no-op |
| `Cooldown Xs` | Cooldown buff in cooldown | whisper time-until-ready, no-op |

The status is a **snapshot at menu-open time** -- it doesn't tick down live. Re-open `/m → My Buffs` to refresh.

### Auto-apply

- **On join / spawn**: every TOGGLE buff whose gate-perm the player holds is applied (unless toggled OFF).
- **Reapply loop**: every `ReapplyIntervalSeconds`, the same check runs -- long-duration toggle buffs get refreshed before expiry. Cooldown buffs are NEVER auto-applied; they're user-triggered only.

### State persistence (`data/StyxBuffs/sessions.json`)

- `UserDisabled[playerId]` -- set of toggle-buff names the player switched OFF. Persists across reconnect / server restart.
- `LastActivated[playerId][buffName]` -- unix timestamp of the most recent activation of a cooldown buff. Used to compute Ready/Active/Cooldown status. Persists, so logging out doesn't reset cooldowns.

### CVars

CVars in `CVars[]` are perm-gated like buffs and applied alongside the auto-apply pass. Useful for `$treatedX`-style flags that XUi labels or other plugins can read.

## Bundled buffs (in `Mods/Styx/Config/buffs.xml`)

| Buff | Effect |
|---|---|
| `buffStyxVipUndead` | +100% damage to zombies |
| `buffStyxVipHarvest` | +100% harvest yield, +50% block damage |
| `buffStyxVipToughness` | +60 phys/elemental resist, +30% run speed, +40 carry, +50 max HP |

You can use any vanilla buff name (`buffDrugSteroids`, `buffMegaCrush`, etc.) or define your own in `Mods/Styx/Config/buffs.xml`.

## Migration from v0.2.x

On first load with an existing v0.2.x `configs/StyxBuffs.json`, the plugin detects the legacy `GroupBuffs` / `GroupCVars` keys and converts them in place:

- Each entry under `GroupBuffs["<group>"]` gets `Perm = "styx.buffs.<group>"` and is moved into the flat `Buffs` list.
- Same shape conversion for `GroupCVars` → `CVars`.
- The legacy keys are removed and the file is rewritten.
- A single log line records the count of migrated entries.

After migration, **the new `styx.buffs.<group>` perms are NOT auto-granted** -- open the perm editor and tick `styx.buffs.vip` on the `vip` group (and `styx.buffs.admin` on `admin`) to restore prior behaviour. This is the whole point of the v0.3.0 redesign: assignment now lives in the editor, not a parallel JSON.

## Notes

- **Re-apply is harmless** -- applying a buff that's already active just refreshes the duration.
- **Custom buffs** must be defined in an XML modlet. See vanilla `Data/Config/buffs.xml` for syntax reference.
- **Set-bonus buffs don't work standalone** -- `buffCommandoSetBonus` etc. require the matching armour set equipped. Granting them via StyxBuffs won't trigger the bonus.
- **Data**: `data/StyxBuffs.sessions.json` -- per-player toggle state.

## Common ops

### Add a custom buff to VIPs

1. Define in `Mods/Styx/Config/buffs.xml`:
   ```xml
   <buff name="buffMyVipPerk" name_key="buffMyVipPerkName" tooltip_key="buffMyVipPerkDesc">
     <stack_type value="replace"/>
     <duration value="3600"/>
     <effect_group>
       <passive_effect name="HealthMax" operation="perc_add" value=".25"/>
     </effect_group>
   </buff>
   ```
2. Add localization in `Mods/Styx/Config/Localization.txt`
3. Append an entry to `Buffs` in `configs/StyxBuffs.json`:
   ```json
   { "Buff": "buffMyVipPerk", "Perm": "styx.buffs.vip", "DurationSeconds": 3600, "DisplayName": "+25% Max HP", "Description": "VIP exclusive", "UserToggleable": true }
   ```
4. Restart server (buff XML changes don't hot-reload).

### Add a new tier of buffs

1. Pick a perm name (e.g. `styx.buffs.elite`).
2. Add buff entries with `"Perm": "styx.buffs.elite"` to `configs/StyxBuffs.json`.
3. Reload the plugin (or restart). The new perm now appears in the perm editor under StyxBuffs.
4. In the perm editor, tick `styx.buffs.elite` on the group(s) that should receive it.

### Force-reapply for everyone after editing

`/buffs reapply`
