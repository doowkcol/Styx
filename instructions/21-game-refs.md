# 21 — StyxGameRefs (dev tool)

Dumps the game's runtime data registries to searchable Markdown reference files. Re-run on demand. Useful for plugin authors and config writers who need to find canonical item/block/entity/buff names.

**Operator note:** Dev/reference tool. Safe to delete from `Mods/Styx/plugins/` if you don't need the refs. The dump runs at server boot and writes to disk — minor startup cost.

## Commands

| Command | Perm | What |
|---|---|---|
| `/gamerefs` | (open) | Re-dump all reference files now |

Auto-runs once at server boot (after all data has loaded).

## Permissions

None — open to all.

## What it produces

Files written to `server/Mods/Styx/data/game_refs/`:

| File | Contents |
|---|---|
| `items.md` | Every `ItemClass` — id, name, max stack, quality flag, tags |
| `blocks.md` | Every `Block` — id, name, class, material, light opacity |
| `entities.md` | Every `EntityClass` — id, name, tags |
| `buffs.md` | Every `BuffClass` — id, name key, hidden flag |

Format is human-readable Markdown tables. Files are **overwritten** each run, so they always match the currently-loaded game data (vanilla + any modlets).

## Why it matters

Plugin authors keep guessing at item/block/entity names because:
- Vanilla XML is huge — hard to grep
- Names rename across patch versions (`drinkJarPureWater` → `drinkJarBoiledWater` in V2.6)
- Modded servers add custom items not in the vanilla XML

Run `/gamerefs` after installing modlets, then grep these files for the canonical name.

## Use cases

### Find the canonical name for "iron pipe"

```
grep -i "iron pipe" Mods/Styx/data/game_refs/items.md
```

### List all zombie entity classes

```
grep zombie Mods/Styx/data/game_refs/entities.md
```

### Validate a Kit config

After editing `configs/Kit.json` with new items, grep `items.md` for each item name to confirm it's spelled correctly. A typo'd item silently fails with a `[Styx] GiveBackpack: item 'X' not found` warning.

## Mechanics

- Runs `OnServerInitialized` (after all XML / data has loaded into engine registries).
- Walks `ItemClass.GetItemClasses()`, `Block.list`, `EntityClass.list`, `BuffClass.list` — same registries the engine itself uses.
- Writes Markdown via simple template formatting.

## Notes

- **Re-run after modlet changes** — if you install a modlet that adds new items, `/gamerefs` to refresh the reference files.
- **Doesn't track changes** — each run overwrites with the current state, no diff history.
- **Roughly 50000 entries total** across the four files — large grep targets but that's the size of the game's data.
- **The auto-dump on boot** logs `[StyxGameRefs] Wrote 4 reference files (50868 total entries)` so you know it ran.

## See also

The framework's `STYX_CAPABILITIES.md` references these files for plugin authors validating item / block / buff names.
