// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// StyxShop -- in-game item shop. Consumes IEconomy, gives items via
// GiveBackpack (the only delivery path that survives the client's
// PlayerData sync -- see STYX_CAPABILITIES.md §10d).
//
// Two files:
//   configs/StyxShop.json           settings (page size, currency display, perm gates)
//                                   framework-watched, hot-reloads on edit
//   data/StyxShop/catalog.json      the actual item list
//                                   reload via /shop reload chat command
//
// Each catalog entry can specify an optional Perm -- players without
// the perm don't see the entry at all (per-player filtered list).
//
// UI: scroll to navigate, LMB to buy, RMB to close.

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Styx;
using Styx.Plugins;

[Info("StyxShop", "Doowkcol", "0.1.0")]
public class StyxShop : StyxPlugin
{
    public override string Description => "Item shop -- buy items with currency, paginated UI";

    private const int PageSize = 8;     // rows visible per page (matches XUi)

    // ============================================================ Config

    public class Config
    {
        // Empty = anyone can use /shop and the launcher entry.
        public string UsePerm = "styx.shop.use";
    }

    public class CatalogEntry
    {
        // Required: canonical item name from items.xml (e.g. "drinkJarBoiledWater").
        public string Item = "";

        // Optional display overrides. Empty = fall back to the item's canonical
        // name / atlas sprite.
        public string DisplayName = "";
        public string Description = "";
        public string Icon = "";

        public int Count = 1;
        public int Quality = 1;
        public long Price = 10;

        // Empty = visible to all (with UsePerm). Set to gate this entry behind
        // a specific perm (e.g. "styx.shop.donor_armour").
        public string Perm = "";

        // Free-form category for the drill-down picker. Empty -> "General".
        // Sections appear in the picker in first-seen order from the catalog.
        public string Section = "";
    }

    public class CatalogFile
    {
        public List<CatalogEntry> Items = new List<CatalogEntry>();
    }

    private Config _cfg;
    private CatalogFile _catalog = new CatalogFile();
    private string _catalogPath;

    // Per-player open-shop state. Two stages:
    //   Stage 0  category picker -- Sections list, Sel/SecPage index into it
    //   Stage 1  item list       -- Filtered = items in CurrentSection,
    //                                Sel/Page index into Filtered
    private class Session
    {
        // Stage 0 state
        public List<string> Sections = new List<string>();          // ordered, perm-filtered sections
        public Dictionary<string, List<int>> ItemsBySection
            = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        public int SecPage;
        public int SecSel;

        // Stage 1 state
        public List<int> Filtered = new List<int>();                // catalog indices in current section
        public string CurrentSection = "";
        public int Page;
        public int Sel;

        public int Stage;   // 0 = sections, 1 = items
    }
    private readonly Dictionary<int, Session> _sessions = new Dictionary<int, Session>();

    // Maps section-name -> stable index for the shop_section_<idx> labels.
    // Built at OnLoad / catalog reload.
    private readonly Dictionary<string, int> _sectionLabelIdx
        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private const string DefaultSection = "General";

    // ============================================================ Lifecycle

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);

        var dataDir = Path.Combine(StyxCore.DataPath(), "StyxShop");
        Directory.CreateDirectory(dataDir);
        _catalogPath = Path.Combine(dataDir, "catalog.json");
        LoadCatalog();

        if (!string.IsNullOrEmpty(_cfg.UsePerm))
            StyxCore.Perms.RegisterKnown(_cfg.UsePerm, "Open the shop UI", Name);
        // Per-entry perms register dynamically as we discover them in the catalog.
        foreach (var e in _catalog.Items)
            if (!string.IsNullOrEmpty(e?.Perm))
                StyxCore.Perms.RegisterKnown(e.Perm, "Shop entry: " + (e.DisplayName != "" ? e.DisplayName : e.Item), Name);

        // Register labels for every catalog entry. Static-baked at shutdown into
        // StyxRuntime/Config/Localization.txt; new entries take effect after a
        // server restart (per the Styx.Ui.Labels lifecycle, §10e).
        BakeLabels();

        // Open/sel/page are session-only -- wipe on spawn so the panel doesn't
        // resurrect after a server restart.
        Styx.Ui.Ephemeral.Register("styx.shop.open", "styx.shop.stage",
            "styx.shop.sel", "styx.shop.page", "styx.shop.page_disp",
            "styx.shop.count", "styx.shop.pages",
            "styx.shop.section_id",
            "styx.shop.detail_id", "styx.shop.detail_price", "styx.shop.detail_count",
            "styx.shop.afford");
        for (int k = 0; k < PageSize; k++)
            Styx.Ui.Ephemeral.Register(
                "styx.shop.row" + k + "_id", "styx.shop.row" + k + "_price",
                "styx.shop.row" + k + "_seccount");

        Styx.Ui.Menu.Register(this, "Shop", OpenFor, permission: _cfg.UsePerm);

        StyxCore.Commands.Register("shop",
            "Open the shop -- /shop [reload]", CmdShop);
        StyxCore.Commands.Register("s",
            "Open the shop (alias for /shop)", CmdShop);

        Log.Out("[StyxShop] Loaded v0.1.0 -- {0} catalog entries", _catalog.Items.Count);
    }

    public override void OnUnload()
    {
        Styx.Ui.Menu.UnregisterAll(this);
        foreach (var eid in _sessions.Keys)
        {
            var p = StyxCore.Player.FindByEntityId(eid);
            if (p == null) continue;
            Styx.Ui.SetVar(p, "styx.shop.open", 0f);
            Styx.Ui.Input.Release(p, Name);
        }
        _sessions.Clear();
    }

    // ============================================================ Catalog IO

    private void LoadCatalog()
    {
        if (!File.Exists(_catalogPath))
        {
            // First run: seed a small example catalog so operators have a
            // starting template they can edit.
            _catalog = new CatalogFile
            {
                Items = new List<CatalogEntry>
                {
                    // ---- Food ----
                    new CatalogEntry { Section = "Food",      Item = "drinkJarBoiledWater",   Count = 3,   Quality = 1, Price = 5,
                                       Description = "Three jars of clean drinking water." },
                    new CatalogEntry { Section = "Food",      Item = "foodCanChili",          Count = 2,   Quality = 1, Price = 15,
                                       Description = "Two cans of chili. Filling and shelf-stable." },
                    new CatalogEntry { Section = "Food",      Item = "foodMeatStew",          Count = 2,   Quality = 1, Price = 25,
                                       Description = "Two bowls of meat stew. Restores hunger fast." },

                    // ---- Medical ----
                    new CatalogEntry { Section = "Medical",   Item = "medicalFirstAidBandage", Count = 5,  Quality = 1, Price = 20,
                                       Description = "Five bandages — treats bleeding wounds." },
                    new CatalogEntry { Section = "Medical",   Item = "medicalAloeCream",      Count = 3,   Quality = 1, Price = 30,
                                       Description = "Three aloe creams for treating abrasions." },

                    // ---- Ammo ----
                    new CatalogEntry { Section = "Ammo",      Item = "ammoArrowIron",         Count = 50,  Quality = 1, Price = 25,
                                       Description = "Fifty iron arrows for hunting and defence." },
                    new CatalogEntry { Section = "Ammo",      Item = "ammo9mmBulletBall",     Count = 60,  Quality = 1, Price = 60,
                                       Description = "Sixty 9mm rounds (ball)." },

                    // ---- Resources ----
                    new CatalogEntry { Section = "Resources", Item = "resourceWood",          Count = 200, Quality = 1, Price = 30,
                                       Description = "Two hundred wood for crafting and building." },
                    new CatalogEntry { Section = "Resources", Item = "resourceForgedIron",    Count = 50,  Quality = 1, Price = 100,
                                       Description = "Fifty forged iron — skip the smelting." },
                    new CatalogEntry { Section = "Resources", Item = "resourceForgedSteel",   Count = 25,  Quality = 1, Price = 250,
                                       Description = "Twenty-five forged steel for high-tier crafting." },
                    new CatalogEntry { Section = "Resources", Item = "resourceConcreteMix",   Count = 100, Quality = 1, Price = 80,
                                       Description = "One hundred concrete mix for fortifications." },

                    // ---- Tools ----
                    new CatalogEntry { Section = "Tools",     Item = "meleeToolRepairT0StoneAxe", Count = 1, Quality = 1, Price = 50,
                                       Description = "Stone axe — for chopping and basic harvesting." },
                    new CatalogEntry { Section = "Tools",     Item = "meleeToolAxeT1IronFireaxe", Count = 1, Quality = 4, Price = 350,
                                       Description = "Tier-1 iron fireaxe at quality 4. Wood goes fast." },
                    new CatalogEntry { Section = "Tools",     Item = "meleeToolPickT1IronPickaxe", Count = 1, Quality = 4, Price = 350,
                                       Description = "Tier-1 iron pickaxe at quality 4. For mining ore." },
                    new CatalogEntry { Section = "Tools",     Item = "meleeToolShovelT1IronShovel", Count = 1, Quality = 4, Price = 250,
                                       Description = "Tier-1 iron shovel at quality 4. Dirt and clay." },

                    // ---- Weapons ----
                    new CatalogEntry { Section = "Weapons",   Item = "gunHandgunT1Pistol",    Count = 1,   Quality = 4, Price = 500,
                                       Description = "Tier-1 9mm pistol at quality 4. Ammo sold separately." },

                    // ---- FX (currency exchange) ----
                    // Convert credits into vanilla dukes (casinoCoin) for spending
                    // at vanilla traders. 200 credits = 1 duke.
                    new CatalogEntry { Section = "FX",        Item = "casinoCoin",            Count = 1,   Quality = 1, Price = 200,
                                       Description = "Exchange credits for vanilla dukes — 200 credits per duke. Spend them at any trader." },
                }
            };
            SaveCatalog();
            return;
        }

        try
        {
            var text = File.ReadAllText(_catalogPath);
            _catalog = JsonConvert.DeserializeObject<CatalogFile>(text) ?? new CatalogFile();
            if (_catalog.Items == null) _catalog.Items = new List<CatalogEntry>();
        }
        catch (Exception e)
        {
            Log.Error("[StyxShop] Catalog load failed: {0}. Using empty catalog.", e.Message);
            _catalog = new CatalogFile();
        }
    }

    private void SaveCatalog()
    {
        try
        {
            var text = JsonConvert.SerializeObject(_catalog, Formatting.Indented);
            File.WriteAllText(_catalogPath, text);
        }
        catch (Exception e) { Log.Error("[StyxShop] Catalog save failed: {0}", e.Message); }
    }

    private void BakeLabels()
    {
        Styx.Ui.Labels.UnregisterAll(this);
        _sectionLabelIdx.Clear();

        for (int i = 0; i < _catalog.Items.Count; i++)
        {
            var e = _catalog.Items[i];
            if (e == null) continue;
            string display = !string.IsNullOrEmpty(e.DisplayName) ? e.DisplayName : ResolveItemDisplayName(e.Item);
            string icon    = !string.IsNullOrEmpty(e.Icon) ? e.Icon : e.Item;  // ItemIconAtlas keys = item names
            Styx.Ui.Labels.Register(this, "shop_name_" + i, display);
            Styx.Ui.Labels.Register(this, "shop_desc_" + i, e.Description ?? "");
            Styx.Ui.Labels.Register(this, "shop_icon_" + i, icon);

            string section = string.IsNullOrEmpty(e.Section) ? DefaultSection : e.Section;
            if (!_sectionLabelIdx.ContainsKey(section))
            {
                int idx = _sectionLabelIdx.Count;
                _sectionLabelIdx[section] = idx;
                Styx.Ui.Labels.Register(this, "shop_section_" + idx, section);
            }
        }
    }

    private static string ResolveItemDisplayName(string itemName)
    {
        // Try to read the localized display name from the engine's item registry.
        // Fall back to the canonical item name if unavailable.
        try
        {
            var ic = ItemClass.GetItemClass(itemName, _caseInsensitive: true);
            if (ic != null && !string.IsNullOrEmpty(ic.GetLocalizedItemName()))
                return ic.GetLocalizedItemName();
        }
        catch { }
        return itemName;
    }

    // ============================================================ Open / Close

    private void OpenFor(EntityPlayer p)
    {
        if (p == null) return;
        var pid = StyxCore.Player.PlatformIdOf(p);

        // Build per-player perm-filtered, section-grouped catalog. Sections
        // are ordered first-seen so operators control display order by
        // ordering items in catalog.json.
        var ses = new Session();
        for (int i = 0; i < _catalog.Items.Count; i++)
        {
            var e = _catalog.Items[i];
            if (e == null || string.IsNullOrEmpty(e.Item)) continue;
            if (!string.IsNullOrEmpty(e.Perm) && !StyxCore.Perms.HasPermission(pid, e.Perm)) continue;

            string section = string.IsNullOrEmpty(e.Section) ? DefaultSection : e.Section;
            if (!ses.ItemsBySection.TryGetValue(section, out var list))
            {
                list = new List<int>();
                ses.ItemsBySection[section] = list;
                ses.Sections.Add(section);
            }
            list.Add(i);
        }

        if (ses.Sections.Count == 0)
        {
            Styx.Server.Whisper(p, "[ffaa00][Shop] No items available to you.[-]");
            return;
        }

        _sessions[p.entityId] = ses;
        Styx.Ui.SetVar(p, "styx.shop.open", 1f);

        // Always start at the section picker -- consistent UX even with
        // a single section. Operator can name it appropriately.
        EnterSectionPicker(p, ses);
        Styx.Ui.Input.Acquire(p, Name);
    }

    private void CloseFor(EntityPlayer p)
    {
        if (p == null) return;
        Styx.Ui.SetVar(p, "styx.shop.open", 0f);
        Styx.Ui.Input.Release(p, Name);
        _sessions.Remove(p.entityId);
    }

    // ============================================================ Stage transitions

    private void EnterSectionPicker(EntityPlayer p, Session ses)
    {
        ses.Stage = 0;
        Styx.Ui.SetVar(p, "styx.shop.stage", 0f);
        Styx.Ui.SetVar(p, "styx.shop.count",
            ses.Sections.Count);   // reused: count of currently-listed items (sections here)
        Styx.Ui.SetVar(p, "styx.shop.pages",
            Math.Max(1, (ses.Sections.Count + PageSize - 1) / PageSize));

        RenderSectionPage(p, ses);
        WhisperSectionRow(p, ses);
    }

    private void EnterSection(EntityPlayer p, Session ses, string section)
    {
        if (!ses.ItemsBySection.TryGetValue(section, out var items) || items.Count == 0) return;
        ses.CurrentSection = section;
        ses.Filtered = items;
        ses.Page = 0;
        ses.Sel  = 0;
        ses.Stage = 1;

        // section_id drives the in-stage1 header label that tells the player
        // which section they're browsing.
        if (_sectionLabelIdx.TryGetValue(section, out var sid))
            Styx.Ui.SetVar(p, "styx.shop.section_id", sid);

        Styx.Ui.SetVar(p, "styx.shop.stage", 1f);
        Styx.Ui.SetVar(p, "styx.shop.count", items.Count);
        Styx.Ui.SetVar(p, "styx.shop.pages",
            Math.Max(1, (items.Count + PageSize - 1) / PageSize));

        RenderItemPage(p, ses);
        WhisperItemRow(p, ses);
    }

    // ============================================================ Render

    private void RenderItemPage(EntityPlayer p, Session ses)
    {
        Styx.Ui.SetVar(p, "styx.shop.page",      ses.Page);
        Styx.Ui.SetVar(p, "styx.shop.page_disp", ses.Page + 1);   // 1-based for the UI
        Styx.Ui.SetVar(p, "styx.shop.sel",       ses.Sel);

        for (int k = 0; k < PageSize; k++)
        {
            int absIdx = ses.Page * PageSize + k;
            if (absIdx < ses.Filtered.Count)
            {
                int catalogIdx = ses.Filtered[absIdx];
                var e = _catalog.Items[catalogIdx];
                Styx.Ui.SetVar(p, "styx.shop.row" + k + "_id", catalogIdx);
                Styx.Ui.SetVar(p, "styx.shop.row" + k + "_price", e.Price);
            }
            else
            {
                Styx.Ui.SetVar(p, "styx.shop.row" + k + "_id", -1);
                Styx.Ui.SetVar(p, "styx.shop.row" + k + "_price", 0);
            }
        }

        UpdateDetail(p, ses);
    }

    private void RenderSectionPage(EntityPlayer p, Session ses)
    {
        Styx.Ui.SetVar(p, "styx.shop.page",      ses.SecPage);
        Styx.Ui.SetVar(p, "styx.shop.page_disp", ses.SecPage + 1);
        Styx.Ui.SetVar(p, "styx.shop.sel",       ses.SecSel);

        for (int k = 0; k < PageSize; k++)
        {
            int absIdx = ses.SecPage * PageSize + k;
            if (absIdx < ses.Sections.Count)
            {
                string section = ses.Sections[absIdx];
                int sid = _sectionLabelIdx.TryGetValue(section, out var v) ? v : 0;
                int itemCount = ses.ItemsBySection[section].Count;
                Styx.Ui.SetVar(p, "styx.shop.row" + k + "_id", sid);
                Styx.Ui.SetVar(p, "styx.shop.row" + k + "_seccount", itemCount);
            }
            else
            {
                Styx.Ui.SetVar(p, "styx.shop.row" + k + "_id", -1);
                Styx.Ui.SetVar(p, "styx.shop.row" + k + "_seccount", 0);
            }
        }
    }

    private void UpdateDetail(EntityPlayer p, Session ses)
    {
        int absIdx = ses.Page * PageSize + ses.Sel;
        if (absIdx >= ses.Filtered.Count)
        {
            Styx.Ui.SetVar(p, "styx.shop.detail_id", -1);
            Styx.Ui.SetVar(p, "styx.shop.detail_price", 0);
            Styx.Ui.SetVar(p, "styx.shop.detail_count", 0);
            Styx.Ui.SetVar(p, "styx.shop.afford", 0);
            return;
        }

        int catalogIdx = ses.Filtered[absIdx];
        var e = _catalog.Items[catalogIdx];
        Styx.Ui.SetVar(p, "styx.shop.detail_id", catalogIdx);
        Styx.Ui.SetVar(p, "styx.shop.detail_price", e.Price);
        Styx.Ui.SetVar(p, "styx.shop.detail_count", e.Count);

        var eco = StyxCore.Services?.Get<IEconomy>();
        long balance = eco?.Balance(p) ?? 0;
        Styx.Ui.SetVar(p, "styx.shop.afford", balance >= e.Price ? 1f : 0f);
    }

    private void WhisperItemRow(EntityPlayer p, Session ses)
    {
        int absIdx = ses.Page * PageSize + ses.Sel;
        if (absIdx >= ses.Filtered.Count) return;
        var e = _catalog.Items[ses.Filtered[absIdx]];
        var eco = StyxCore.Services?.Get<IEconomy>();
        string currency = eco?.CurrencyName ?? "Credits";
        Styx.Server.Whisper(p, string.Format(
            "[ccddff][{0}] {1}/{2}:[-] [ffffdd]{3} x{4} — {5} {6}[-]",
            ses.CurrentSection, absIdx + 1, ses.Filtered.Count,
            !string.IsNullOrEmpty(e.DisplayName) ? e.DisplayName : ResolveItemDisplayName(e.Item),
            e.Count, e.Price, currency));
    }

    private void WhisperSectionRow(EntityPlayer p, Session ses)
    {
        int absIdx = ses.SecPage * PageSize + ses.SecSel;
        if (absIdx >= ses.Sections.Count) return;
        string section = ses.Sections[absIdx];
        int n = ses.ItemsBySection[section].Count;
        Styx.Server.Whisper(p, string.Format(
            "[ccddff][Shop] {0}/{1}:[-] [ffffdd]{2} ({3} item{4})[-]",
            absIdx + 1, ses.Sections.Count, section, n, n == 1 ? "" : "s"));
    }

    // ============================================================ Input

    void OnPlayerInput(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (p == null || !_sessions.TryGetValue(p.entityId, out var ses)) return;

        if (ses.Stage == 0) HandleSectionInput(p, ses, kind);
        else                HandleItemInput(p, ses, kind);
    }

    private void HandleSectionInput(EntityPlayer p, Session ses, Styx.Ui.StyxInputKind kind)
    {
        int total = ses.Sections.Count;
        if (total == 0) return;
        int abs = ses.SecPage * PageSize + ses.SecSel;

        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
                abs = (abs + 1) % total;
                ses.SecPage = abs / PageSize;
                ses.SecSel  = abs % PageSize;
                RenderSectionPage(p, ses);
                WhisperSectionRow(p, ses);
                break;
            case Styx.Ui.StyxInputKind.Crouch:
                abs = (abs - 1 + total) % total;
                ses.SecPage = abs / PageSize;
                ses.SecSel  = abs % PageSize;
                RenderSectionPage(p, ses);
                WhisperSectionRow(p, ses);
                break;
            case Styx.Ui.StyxInputKind.PrimaryAction:
                if (abs < ses.Sections.Count)
                    EnterSection(p, ses, ses.Sections[abs]);
                break;
            case Styx.Ui.StyxInputKind.SecondaryAction:
                // RMB on section picker -> close shop, return to /m launcher.
                CloseFor(p);
                Styx.Scheduling.Scheduler.Once(0.05,
                    () => Styx.Ui.Menu.OpenLauncher?.Invoke(p),
                    name: "StyxShop.BackToLauncher");
                break;
        }
    }

    private void HandleItemInput(EntityPlayer p, Session ses, Styx.Ui.StyxInputKind kind)
    {
        int total = ses.Filtered.Count;
        if (total == 0) { EnterSectionPicker(p, ses); return; }
        int abs = ses.Page * PageSize + ses.Sel;

        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
                abs = (abs + 1) % total;
                ses.Page = abs / PageSize;
                ses.Sel  = abs % PageSize;
                RenderItemPage(p, ses);
                WhisperItemRow(p, ses);
                break;
            case Styx.Ui.StyxInputKind.Crouch:
                abs = (abs - 1 + total) % total;
                ses.Page = abs / PageSize;
                ses.Sel  = abs % PageSize;
                RenderItemPage(p, ses);
                WhisperItemRow(p, ses);
                break;
            case Styx.Ui.StyxInputKind.PrimaryAction:
                TryBuy(p, ses);
                break;
            case Styx.Ui.StyxInputKind.SecondaryAction:
                // RMB in item list -> back to section picker.
                EnterSectionPicker(p, ses);
                break;
        }
    }

    private void TryBuy(EntityPlayer p, Session ses)
    {
        int abs = ses.Page * PageSize + ses.Sel;
        if (abs >= ses.Filtered.Count) return;
        var entry = _catalog.Items[ses.Filtered[abs]];
        if (entry == null) return;

        var eco = StyxCore.Services?.Get<IEconomy>();
        if (eco == null)
        { Styx.Server.Whisper(p, "[ff6666][Shop] Economy not loaded.[-]"); return; }

        if (!eco.TryDebit(p, entry.Price, "shop " + entry.Item))
        {
            Styx.Server.Whisper(p, string.Format(
                "[ff6666][Shop] Insufficient {0} (need {1}, have {2}).[-]",
                eco.CurrencyName, entry.Price, eco.Balance(p)));
            return;
        }

        // Deliver the item. Backpack-drop is the only delivery path that
        // survives the client's PlayerData sync (see §10d).
        bool ok = StyxCore.Player.GiveBackpack(p,
            new[] { (entry.Item, entry.Count, entry.Quality) });

        if (!ok)
        {
            // Refund on delivery failure (e.g. unknown item name).
            eco.Credit(p, entry.Price, "refund -- delivery failed");
            Styx.Server.Whisper(p, string.Format(
                "[ff6666][Shop] Delivery failed for '{0}'. {1} refunded.[-]",
                entry.Item, entry.Price));
            return;
        }

        Styx.Server.Whisper(p, string.Format(
            "[00ff66][Shop] Bought {0} x{1} for {2} {3}. Balance: {4}.[-]",
            !string.IsNullOrEmpty(entry.DisplayName) ? entry.DisplayName : ResolveItemDisplayName(entry.Item),
            entry.Count, entry.Price, eco.CurrencyName, eco.Balance(p)));

        // Refresh the affordability indicator after the debit.
        UpdateDetail(p, ses);
    }

    // ============================================================ Commands

    private void CmdShop(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }

        if (args.Length > 0 && args[0].ToLowerInvariant() == "reload")
        {
            var actorId = StyxCore.Player.PlatformIdOf(StyxCore.Player.FindByEntityId(ctx.Client.entityId));
            if (!StyxCore.Perms.HasPermission(actorId, "styx.eco.admin"))
            { ctx.Reply("[ff6666]No permission.[-]"); return; }
            LoadCatalog();
            BakeLabels();
            ctx.Reply(string.Format("[00ff66][Shop] Catalog reloaded -- {0} entries. Restart server to refresh display labels.[-]",
                _catalog.Items.Count));
            return;
        }

        var p = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
        if (p == null) { ctx.Reply("Player not found."); return; }
        if (!string.IsNullOrEmpty(_cfg.UsePerm) &&
            !StyxCore.Perms.HasPermission(StyxCore.Player.PlatformIdOf(p), _cfg.UsePerm))
        { ctx.Reply("[ff6666]No permission to use the shop.[-]"); return; }

        OpenFor(p);
    }
}
