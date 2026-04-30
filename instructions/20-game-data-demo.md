# 20 — GameDataDemo (dev tool)

Demo plugin for the Styx v0.6 GameData runtime-mutation API. Lets you read/modify items, recipes, and buffs at runtime — every mutation is automatically reverted on plugin unload (hot-reload safe).

**Operator note:** This is a dev/diagnostic tool. Safe to delete from `Mods/Styx/plugins/` for production servers. Don't use the `fast` subcommand on a production server unless you want to permanently halve your forge times for fun.

## Commands

| Command | Perm | What |
|---|---|---|
| `/gd probe <itemName>` | (open) | Read a few fields from the item (max stack, quality flag, etc.) |
| `/gd fast` | (open) | Halve all forge recipe craft times |
| `/gd slow` | (open) | Double all forge recipe craft times |
| `/gd buff <buffName>` | (open) | Read fields from a buff |
| `/gd undo` | (open) | Trigger plugin reload to revert mutations |

## Permissions

None — open to all (dev tool, intended for testing).

## What it demonstrates

- **Read API**: `StyxCore.GameData.Items.Get(name)`, `.Recipes.Get(name)`, `.Buffs.Get(name)` — returns wrappers around the engine's loaded data.
- **Mutation API**: `.SetProperty()` etc. — modifies cached engine values.
- **Auto-revert**: every mutation is recorded by `GameDataManager`. On plugin unload (which fires on hot-reload too), all mutations are rolled back. No persistence, no XML editing.

## Why it matters

Plugin authors can do things like:
- Halve craft times for a "double XP weekend" event without touching XML
- Boost loot for an in-game event, then revert when it ends
- Test recipe tweaks live without restarting the server

All without editing `recipes.xml` / `items.xml` / `buffs.xml` and risking breaking the modlet pipeline.

## Mechanics

- `/gd fast` walks every recipe with `craft_area="forge"` and divides `craftingTime` by 2.
- `/gd slow` does the inverse.
- `/gd undo` triggers plugin reload → unload fires → every recorded mutation reverts.
- `/gd probe` just reads fields (no mutation).

## Notes

- **Hot-reload safe** — saving the .cs file or `/gd undo` reverts all changes.
- **Server-restart safe** — on shutdown, mutations don't persist (they're cached engine values, not XML).
- **Doesn't change XML on disk** — pure runtime mutation.
- **Affects ALL recipes** of the matching type — `/gd fast` halves every forge recipe globally, not perm-tiered.
- **For perm-tiered crafting**, use **StyxCrafting** (which uses Harmony patches on `TileEntityWorkstation.read` for per-player scaling).

## See also

- `STYX_CAPABILITIES.md §5` — full GameData API surface
- `STYX_GAMEDATA_POSTULATE.md` — design notes
- `instructions/05-crafting.md` — the per-perm production version (StyxCrafting)
