# 27 — StyxLeveling

Server-meta XP + level system. **Completely separate from vanilla 7DTD player level / skill points** — this is a parallel progression you control.

Players accumulate XP from configured StyxRewards earn paths (or admin grants), levels compute from a quadratic curve, and reaching configurable milestone levels promotes them into perm groups that you control. Group memberships stack — reaching level 100 keeps your level 25/50/75 group memberships and all their perks.

## Commands

| Command | What | Perm |
|---|---|---|
| `/xp` | Show your level + XP + progress to next | open |
| `/xp <player>` | Query another player | `styx.xp.admin` |
| `/xp give <player> <amount>` | Add XP | `styx.xp.admin` |
| `/xp take <player> <amount>` | Subtract XP | `styx.xp.admin` |
| `/xp set <player> <amount>` | Set XP to exact value | `styx.xp.admin` |
| `/xp wipe confirm` | Clear ALL XP + remove every player from every milestone group (auto-backup) | `styx.xp.admin` |

## Curve

Cumulative XP for level L is `BaseXp · L^Exponent`. Defaults match the SkillTree (Rust) curve exactly:

```json
"BaseXp":   650.0,    // XP for level 1
"Exponent": 2.0,      // quadratic
"MaxLevel": 100
```

Reference points with defaults: L1=650, L25=406,250, L50=1,625,000, L75=3,656,250, L100=6,500,000.

### Custom override

For surgical control, set `CustomXpTable` (keys are level numbers as strings):
```json
"CustomXpTable": {
  "1": 1000,
  "2": 3000,
  "5": 25000,
  ...
}
```

Any level NOT listed falls back to the formula. Empty (default) = formula only.

## Milestones

```json
"Milestones": [
  {
    "Level": 25,
    "Group": "lvl25",
    "Broadcast": "{name} reached level 25!",
    "Whisper":   "[00FF66]You earned level 25 bonuses![-]"
  },
  {
    "Level": 50,
    "Group": "lvl50",
    "Broadcast": "{name} reached level 50!",
    "Whisper":   "[00FF66]You earned level 50 bonuses![-]"
  },
  ...
]
```

When a player crosses a milestone level:
1. Added to the named perm group (additive — stacks with prior milestones)
2. Server-wide chat broadcast (`{name}` substitutes)
3. Private whisper to the player (`{name}` substitutes)

`Group` is the only required action — you can set `Broadcast` / `Whisper` to empty strings to skip that announcement.

The milestone groups are pre-created at plugin load so PermEditor + `/perm` tooling see them immediately, even before any player has reached the level.

## Server ranks via chat tags

Reaching a milestone gives the player a perm-group membership. Combine with the existing chat-tag-priority system to make milestone groups display as ranks:

```
/perm group prio lvl25  100
/perm group tag  lvl25  "[Veteran]"  ffaa66

/perm group prio lvl50  200
/perm group tag  lvl50  "[Elite]"    ff66cc

/perm group prio lvl75  300
/perm group tag  lvl75  "[Master]"   66ddff

/perm group prio lvl100 400
/perm group tag  lvl100 "[Apex]"     ffff00
```

Highest priority wins — a player who's reached level 75 sees `[Master]` even though they're also in `lvl25` and `lvl50` (those have lower priorities).

## Granting actual perks per milestone

The milestone groups don't have any perms by default — you decide what reaching level 25 unlocks. Examples:

```
/perm group grant lvl25  styx.kit.veteran        # unlocks the "veteran" kit
/perm group grant lvl25  styx.eco.x2             # 2x reward multiplier
/perm group grant lvl50  styx.shop.donor         # see donor-only shop entries
/perm group grant lvl50  styx.craft.vip          # better crafting buffs
/perm group grant lvl75  styx.tp.master          # max-tier teleport
/perm group grant lvl100 styx.eco.x3             # 3x reward multiplier (replaces x2 -- first-match-wins)
```

This is where the system shines — every plugin's perm gates can become level-driven.

## HUD integration

When this plugin loads, `styxHud` shows two extra rows:
```
Level: 27
XP:    474411 / 510204
```

Hides automatically if you uninstall the plugin.

## Wipe schedule (independent from money)

`/xp wipe confirm` clears all XP **and** removes every player from every configured milestone group. A backup `wallet.wiped-<UTC-timestamp>.json` is written first.

Money and XP wipes are independent — operators can:
- Wipe money weekly, leave XP alone (rolling economy on persistent rank)
- Wipe XP yearly, leave money alone (long-form progression with current cash)
- Wipe both on map reset
- Never wipe (lifetime account progression)

Schedule via OS task scheduler / Pterodactyl cron / RCON — no built-in auto-wipe.

### Locking wipe on test / sandbox servers

On a test server where visitors get auto-admin (auth-0), `styx.xp.admin` is implicitly granted to every joiner — including `/xp wipe`. To lock it down, set:

```json
"WipeAdditionalPerm": "ops.wipe"
```

`/xp wipe` will then require **both** `styx.xp.admin` AND `ops.wipe`. The `ops.wipe` perm starts with `ops.` (not `styx.`) so it's NOT auto-granted to auth-0 owners. Only the operator who explicitly grants it to themselves has it:

```
/perm grant <YourSteamId> ops.wipe
```

Same pattern available on StyxEconomy for `/eco wipe` — re-use the same `ops.wipe` perm for both.

## Programmatic access

```csharp
var lvl = StyxCore.Services?.Get<ILeveling>();
if (lvl == null) return;

long xp = lvl.Xp(player);
int  L  = lvl.Level(player);
long need = lvl.XpForLevel(L + 1);
lvl.AddXp(player, 100, "found rare item");
```

## Where state lives

- XP: `data/StyxLeveling/wallet.json` (atomic-write)
- Milestone groups: stored in the standard Styx perm system (`data/permissions.json`) — you can `/perm group inspect lvl25` like any other group

## See also

- [01 — Permissions](./01-permissions.md) — the chat-tag and group inheritance system milestones plug into
- [25 — StyxRewards](./25-rewards.md) — the engine that calls `ILeveling.AddXp` from kills / loot / quests / etc.
