# 24 — StyxEconomy

The currency bank. Pure server-side virtual currency — no in-world item, no XML, no client mod. Every player has a wallet that survives logout. Other plugins (StyxRewards, StyxShop, future Tebex bridge) read/write this via the `IEconomy` service.

## Commands

| Command | What | Perm |
|---|---|---|
| `/balance` | Show your balance | open |
| `/balance <player>` | Show another player's balance | `styx.eco.admin` |
| `/pay <player> <amount>` | Transfer credits to another online player | `styx.eco.pay` |
| `/eco grant <player> <amt> [reason]` | Add credits to a player | `styx.eco.admin` |
| `/eco take <player> <amt> [reason]` | Subtract credits | `styx.eco.admin` |
| `/eco set <player> <amt>` | Set balance to an exact value | `styx.eco.admin` |
| `/eco wipe confirm` | Clear EVERY balance (auto-backup first) | `styx.eco.admin` |

## Config (`configs/StyxEconomy.json`)

```json
{
  "CurrencyName": "Credits",     // displayed in chat + HUD
  "DriveHudCvar": true,           // push styx.eco.balance to drive the HUD row
  "LogTransactions": false        // verbose Credit/Debit logging
}
```

That's the entire config — economy is intentionally lean. Earn rates live in StyxRewards, not here.

## HUD integration

When this plugin loads, `styxHud` shows a `<CurrencyName>: <amount>` row. Hides automatically if you uninstall the plugin.

The currency name "Credits" can be renamed — edit `CurrencyName` and restart twice (once to stage the new label at shutdown, once to load it on next boot).

## Wipe schedule

There's no auto-wipe. Operator triggers `/eco wipe confirm` manually, or schedules it via OS task scheduler / Pterodactyl cron. A backup `wallet.wiped-<UTC-timestamp>.json` is written next to the live `wallet.json` before the wipe — recoverable if you regret it.

### Locking wipe on test / sandbox servers

On a test server where visitors get auto-admin (auth-0), `styx.eco.admin` is implicitly granted to every joiner — including `/eco wipe`. To keep visitors from accidentally (or deliberately) nuking everyone's wallets, set:

```json
"WipeAdditionalPerm": "ops.wipe"
```

`/eco wipe` will then require **both** `styx.eco.admin` AND `ops.wipe`. The `ops.wipe` perm starts with `ops.` (not `styx.`) so it's NOT auto-granted to auth-0 owners — only the operator who explicitly grants it to themselves has it:

```
/perm grant <YourSteamId> ops.wipe
```

Visitors get a clear refusal message when they try `/eco wipe`. Same pattern available on StyxLeveling for `/xp wipe`.

## Programmatic access (for plugin authors)

```csharp
var eco = StyxCore.Services?.Get<IEconomy>();
if (eco == null) return;   // economy not installed -- skip gracefully

long bal = eco.Balance(player);
eco.Credit(player, 50, "quest reward");
if (eco.TryDebit(player, 200, "shop purchase")) {
    // gave the item
}
```

`IEconomy` lives in `Styx.Core.Plugins`. Both publisher and consumer reference the same interface type via `Styx.Core.dll`, so cross-plugin lookups don't need reflection.

## Where state lives

- Balances: `data/StyxEconomy/wallet.json` (atomic-write, survives crashes)
- No transient state — everything balance-related is in this one file

## See also

- [25 — StyxRewards](./25-rewards.md) — the earn engine that calls `IEconomy.Credit`
- [26 — StyxShop](./26-shop.md) — calls `IEconomy.TryDebit` on purchase
