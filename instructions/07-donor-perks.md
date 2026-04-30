# 07 — DonorPerks

Maps permission groups to lists of buffs (and optional CVar writes). Buffs auto-apply on join/spawn and re-apply periodically (so long buffs don't silently drop). Players can toggle individual buffs ON/OFF via the picker UI.

## Commands

| Command | Perm | What |
|---|---|---|
| `/donor` | (open) | Show your active donor perks |
| `/donor status` | (open) | Same as `/donor` |
| `/donor reapply` | `styx.donor.admin` | Force re-apply for everyone now (debugging) |
| `/m → My Buffs` | `styx.donor.use` | Picker UI to toggle your own buffs ON/OFF |

## Permissions

| Perm | What |
|---|---|
| `styx.donor.use` | Required to open the `/m → My Buffs` toggle UI |
| `styx.donor.admin` | Force-reapply for all players, view everyone's perk state |

Eligibility for any given buff is determined by **group membership**, mapped in config.

## Config — `configs/DonorPerks.json`

```json
{
  "ReapplyIntervalSeconds": 1800,
  "GroupBuffs": {
    "vip": [
      {
        "Buff": "buffStyxVipUndead",
        "DurationSeconds": 3600,
        "DisplayName": "VIP: Undead Slayer",
        "Description": "+100% damage to zombies",
        "UserToggleable": true,
        "Icon": "ui_game_symbol_fist"
      },
      {
        "Buff": "buffStyxVipHarvest",
        "DurationSeconds": 3600,
        "DisplayName": "VIP: Harvest Master",
        "Description": "+100% harvest, +50% block damage",
        "UserToggleable": true,
        "Icon": "ui_game_symbol_tool"
      },
      {
        "Buff": "buffStyxVipToughness",
        "DurationSeconds": 3600,
        "DisplayName": "VIP: Toughness",
        "Description": "+60 phys/elemental resist, +30% run, +40 carry, +50 max HP",
        "UserToggleable": true,
        "Icon": "ui_game_symbol_armor_iron"
      }
    ],
    "admin": [
      { "Buff": "buffDrugSteroids", "DurationSeconds": 3600, "DisplayName": "Steroids", "UserToggleable": false }
    ]
  },
  "GroupCVars": {
    "vip": [ { "Name": "$treatedVip", "Value": 1.0 } ]
  }
}
```

| Field | Meaning |
|---|---|
| `ReapplyIntervalSeconds` | How often the periodic re-apply sweep runs (default 30 min) |
| `GroupBuffs.<group>` | List of `BuffEntry` granted to anyone in that group |
| `BuffEntry.Buff` | Buff name (must be defined in vanilla or a modlet) |
| `BuffEntry.DurationSeconds` | Override duration on apply; default 3600 |
| `BuffEntry.DisplayName` | Optional UI label; falls back to `Buff` if null/empty |
| `BuffEntry.Description` | Whisper text shown when this row is highlighted in `/m → My Buffs` |
| `BuffEntry.UserToggleable` | False = player can't disable this buff |
| `BuffEntry.Icon` | Optional UIAtlas sprite name |
| `GroupCVars.<group>[].Name` | Entity CVar to set (e.g. `$treatedVip`) |
| `GroupCVars.<group>[].Value` | Float value to write |

## Mechanics

- **On join / spawn**: every buff mapped to any group the player belongs to gets applied (unless they've toggled it OFF and `UserToggleable` is true).
- **Reapply loop**: every `ReapplyIntervalSeconds`, the same check runs. Long buffs (e.g. 1-hour donor buffs) get refreshed before they expire.
- **Player toggle**: `/m → My Buffs` opens a picker. Each row shows the buff's display name + description + current ON/OFF state. LMB toggles. OFF persists across sessions (`data/DonorPerks.sessions.json`).
- **Toggle OFF**: skipped by the reapply loop, AND the buff is actively removed if currently active. Stays off until the player toggles back ON.
- **GroupCVars**: any CVars listed for a group are also written on apply — useful for `$treatedX`-style flags that XUi labels or other plugins can read.

## Bundled buffs (in `Mods/Styx/Config/buffs.xml`)

| Buff | Effect |
|---|---|
| `buffStyxVipUndead` | +100% damage to zombies |
| `buffStyxVipHarvest` | +100% harvest yield, +50% block damage |
| `buffStyxVipToughness` | +60 phys/elemental resist, +30% run speed, +40 carry, +50 max HP |

You can use any vanilla buff name (`buffDrugSteroids`, `buffMegaCrush`, etc.) or define your own in `Mods/Styx/Config/buffs.xml`.

## Notes

- **Group membership = donor tier** — manage via `/perm addto <player> vip`.
- **Re-apply is harmless** — applying a buff that's already active just refreshes the duration.
- **Custom buffs** must be defined in an XML modlet. See vanilla `Data/Config/buffs.xml` for syntax reference.
- **Set-bonus buffs don't work standalone** — `buffCommandoSetBonus` etc. require the matching armour set equipped. Granting them via DonorPerks won't trigger the bonus.
- **Data**: `data/DonorPerks.sessions.json` — per-player toggle state.

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
3. Add an entry under the right group in `configs/DonorPerks.json`:
   ```json
   { "Buff": "buffMyVipPerk", "DurationSeconds": 3600, "DisplayName": "+25% Max HP", "Description": "VIP exclusive", "UserToggleable": true }
   ```
4. Restart server (buff XML changes don't hot-reload).

### Force-reapply for everyone after editing

`/donor reapply`
