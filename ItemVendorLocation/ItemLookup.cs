﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud;
using Dalamud.Logging;
using ItemVendorLocation.Models;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel;
using Lumina.Excel.GeneratedSheets;

namespace ItemVendorLocation
{
#if DEBUG
    public class ItemLookup
#else
    internal class ItemLookup
#endif
    {
        private readonly ExcelSheet<CustomTalk> _customTalks;
        private readonly ExcelSheet<Achievement> _achievements;
        private readonly ExcelSheet<CustomTalkNestHandlers> _customTalkNestHandlers;

        private readonly ExcelSheet<ENpcBase> _eNpcBases;
        private readonly ExcelSheet<ENpcResident> _eNpcResidents;

        private readonly ExcelSheet<FateShopCustom> _fateShops;
        private readonly ExcelSheet<GilShopItem> _gilShopItems;
        private readonly ExcelSheet<GilShop> _gilShops;
        private readonly ExcelSheet<SpecialShopCustom> _specialShops;
        private readonly ExcelSheet<GCShop> _gcShops;
        private readonly ExcelSheet<GCScripShopItem> _gcScripShopItems;
        private readonly ExcelSheet<GCScripShopCategory> _gcScripShopCategories;
        private readonly ExcelSheet<InclusionShop> _inclusionShops;
        private readonly ExcelSheet<InclusionShopSeriesCustom> _inclusionShopSeries;
        private readonly ExcelSheet<FccShop> _fccShops;
        private readonly ExcelSheet<PreHandler> _preHandlers;
        private readonly ExcelSheet<TopicSelect> _topicSelects;
        private readonly ExcelSheet<CollectablesShop> _collectablesShops;
        private readonly ExcelSheet<CollectablesShopItem> _collectablesShopItems;

        private readonly ExcelSheet<TerritoryType> _territoryType;

        private readonly Item _gil;
        private readonly ExcelSheet<Item> _items;
        private readonly List<Item> _gcSeal;
        private readonly Addon _fccName;

        private readonly Dictionary<uint, ItemInfo> _itemDataMap = new();
        private readonly Dictionary<uint, NpcLocation> _npcLocations = new();

        private bool _isDataReady;

        private readonly Dictionary<uint, uint> _shbFateShopNpc = new()
        {
            { 1027998, 1769957 },
            { 1027538, 1769958 },
            { 1027385, 1769959 },
            { 1027497, 1769960 },
            { 1027892, 1769961 },
            { 1027665, 1769962 },
            { 1027709, 1769963 },
            { 1027766, 1769964 },
        };

        private readonly uint FirstSpecialShopId;
        private readonly uint LastSpecialShopId;


        public ItemLookup()
        {
            _eNpcBases = Service.DataManager.GetExcelSheet<ENpcBase>();
            _eNpcResidents = Service.DataManager.GetExcelSheet<ENpcResident>();
            _gilShopItems = Service.DataManager.GetExcelSheet<GilShopItem>();
            _gilShops = Service.DataManager.GetExcelSheet<GilShop>();
            _specialShops = Service.DataManager.GetExcelSheet<SpecialShopCustom>();
            _customTalks = Service.DataManager.GetExcelSheet<CustomTalk>();
            _customTalkNestHandlers = Service.DataManager.GetExcelSheet<CustomTalkNestHandlers>();
            _fateShops = Service.DataManager.GetExcelSheet<FateShopCustom>();
            _territoryType = Service.DataManager.GetExcelSheet<TerritoryType>();

            _gcScripShopItems = Service.DataManager.GetExcelSheet<GCScripShopItem>();
            _gcShops = Service.DataManager.GetExcelSheet<GCShop>();
            _gcScripShopCategories = Service.DataManager.GetExcelSheet<GCScripShopCategory>();

            _inclusionShops = Service.DataManager.GetExcelSheet<InclusionShop>();
            _inclusionShopSeries = Service.DataManager.GetExcelSheet<InclusionShopSeriesCustom>();
            _fccShops = Service.DataManager.GetExcelSheet<FccShop>();
            _preHandlers = Service.DataManager.GetExcelSheet<PreHandler>();
            _topicSelects = Service.DataManager.GetExcelSheet<TopicSelect>();
            _collectablesShops = Service.DataManager.GetExcelSheet<CollectablesShop>();
            _collectablesShopItems = Service.DataManager.GetExcelSheet<CollectablesShopItem>();

            _achievements = Service.DataManager.GetExcelSheet<Achievement>();

            _fccName = Service.DataManager.GetExcelSheet<Addon>().GetRow(102233);

            _items = Service.DataManager.GetExcelSheet<Item>();
            _gil = _items.GetRow(1);

            _gcSeal = _items.Where(i => i.RowId is >= 20 and <= 22).Select(i => i).ToList();

            FirstSpecialShopId = _specialShops.First().RowId;
            LastSpecialShopId = _specialShops.Last().RowId;

            _ = Task.Run(async () =>
            {
                while (!Service.DataManager.IsDataReady)
                {
                    await Task.Delay(500);
                }

                BuildNpcLocation();
                BuildVendors();
                AddAchievementItem();
                PostProcess();
                _isDataReady = true;
#if DEBUG
                Dictionary<string, uint> noLocationNpcs = new();
                foreach (KeyValuePair<uint, ItemInfo> items in _itemDataMap)
                {
                    foreach (NpcInfo npc in items.Value.NpcInfos)
                    {
                        if (npc.Location == null)
                        {
                            if (!noLocationNpcs.TryAdd(npc.Name, 1))
                            {
                                noLocationNpcs[npc.Name]++;
                            }
                        }
                    }
                }
                PluginLog.Debug("Data is ready");
                PluginLog.Debug($"Items sold by NPCs with no location: {noLocationNpcs.Values.Aggregate((sum, i) => sum += i)}");
                PluginLog.Debug("Named NPCs:");
                foreach (KeyValuePair<string, uint> npc in noLocationNpcs)
                {
                    if (char.IsUpper(npc.Key.First()))
                    {
                        PluginLog.Debug($"{npc.Key} sells {npc.Value} items");
                    }
                }
                PluginLog.Debug("Unnamed NPCs:");
                foreach (KeyValuePair<string, uint> npc in noLocationNpcs)
                {
                    if (!char.IsUpper(npc.Key.First()))
                    {
                        PluginLog.Debug($"{npc.Key} sells {npc.Value} items");
                    }
                }
#endif
            });
        }

        // Post processing item map, can be used to fix things
        private void PostProcess()
        {
            // This fix is for non-japanese client
            // SE is just being lazy on this, hence we have this bug lol
            if (Service.ClientState.ClientLanguage != ClientLanguage.Japanese)
            {
                // Look for items that can be purchased from this npc
                foreach (KeyValuePair<uint, ItemInfo> item in _itemDataMap)
                {
                    foreach (NpcInfo npcInfo in item.Value.NpcInfos)
                    {
                        if (npcInfo.ShopName == "アイテムの購入")
                        {
                            PluginLog.Debug($"{_items.GetRow(item.Key).Name} has ShopName \"アイテムの購入\", correcting to correct one.");
                            // This correction is for Aenc Ose, who sells "Sheep Equipment Materials", for example.
                            // A shop is the sub-menu presented at some vendors. Aenc Ose has no such sub-menu, so we simply remove the shop.
                            npcInfo.ShopName = null;
                        }
                    }
                }
            }
        }


        // https://discord.com/channels/581875019861328007/653504487352303619/860865002721247261
        // https://github.com/SapphireServer/Sapphire/blob/a5c15f321f7e795ed7362ae15edaada99ca7d9be/src/world/Manager/EventMgr.cpp#L14
        private static bool MatchEventHandlerType(uint data, EventHandlerType type)
        {
            return ((data >> 16) & (uint)type) == (uint)type;
        }

#if DEBUG
        public void BuildDebugVendorInfo(uint vendorId)
        {
            ENpcBase npcBase = _eNpcBases.GetRow(vendorId);
            if (npcBase == null)
            {
                return;
            }

            BuildVendorInfo(npcBase);
        }
#endif

        private void BuildVendors()
        {
            foreach (ENpcBase npcBase in _eNpcBases)
            {
                if (npcBase == null)
                {
                    continue;
                }
                BuildVendorInfo(npcBase);
            }
        }

        private void BuildVendorInfo(ENpcBase npcBase)
        {
            ENpcResident resident = _eNpcResidents.GetRow(npcBase.RowId);

            if (HackyFix_Npc(npcBase, resident))
            {
                return;
            }

            FateShopCustom fateShop = _fateShops.GetRow(npcBase.RowId);
            if (fateShop != null)
            {
                foreach (LazyRow<SpecialShop> specialShop in fateShop.SpecialShop)
                {
                    if (specialShop.Value == null)
                    {
                        continue;
                    }

                    SpecialShopCustom specialShopCustom = _specialShops.GetRow(specialShop.Row);
                    AddSpecialItem(specialShopCustom, npcBase, resident);
                }

                return;
            }

            foreach (uint npcData in npcBase.ENpcData)
            {
                if (npcData == 0)
                {
                    continue;
                }

                InclusionShop inclusionShop = _inclusionShops.GetRow(npcData);
                FccShop fccShop = _fccShops.GetRow(npcData);
                PreHandler preHandler = _preHandlers.GetRow(npcData);
                TopicSelect topicSelect = _topicSelects.GetRow(npcData);

                AddInclusionShop(inclusionShop, npcBase, resident);
                AddFccShop(fccShop, npcBase, resident);
                AddItemsInPrehandler(preHandler, npcBase, resident);
                AddItemsInTopicSelect(topicSelect, npcBase, resident);

                if (MatchEventHandlerType(npcData, EventHandlerType.GcShop))
                {
                    GCShop gcShop = _gcShops.GetRow(npcData);
                    AddGcShopItem(gcShop, npcBase, resident);
                    continue;
                }

                if (MatchEventHandlerType(npcData, EventHandlerType.SpecialShop))
                {
                    SpecialShopCustom specialShop = _specialShops.GetRow(npcData);
                    AddSpecialItem(specialShop, npcBase, resident);
                    continue;
                }

                if (MatchEventHandlerType(npcData, EventHandlerType.GilShop))
                {
                    GilShop gilShop = _gilShops.GetRow(npcData);
                    AddGilShopItem(gilShop, npcBase, resident);
                    continue;
                }

                if (MatchEventHandlerType(npcData, EventHandlerType.CustomTalk))
                {
                    CustomTalk customTalk = _customTalks.GetRow(npcData);
                    if (customTalk == null)
                    {
                        continue;
                    }

                    if (customTalk.SpecialLinks != 0)
                    {
                        try
                        {
                            for (uint index = 0; index <= 30; index++)
                            {
                                CustomTalkNestHandlers customTalkNestHandler = _customTalkNestHandlers.GetRow(customTalk.SpecialLinks, index);
                                if (customTalkNestHandler != null)
                                {
                                    SpecialShopCustom specialShop = _specialShops.GetRow(customTalkNestHandler.NestHandler);
                                    if (specialShop != null)
                                    {
                                        AddSpecialItem(specialShop, npcBase, resident);
                                    }
                                    GilShop gilShop = _gilShops.GetRow(customTalkNestHandler.NestHandler);
                                    if (gilShop != null)
                                    {
                                        AddGilShopItem(gilShop, npcBase, resident);
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    foreach (uint arg in customTalk.ScriptArg)
                    {
                        if (MatchEventHandlerType(arg, EventHandlerType.GilShop))
                        {
                            GilShop gilShop = _gilShops.GetRow(arg);
                            AddGilShopItem(gilShop, npcBase, resident);
                            continue;
                        }

                        if (MatchEventHandlerType(arg, EventHandlerType.FcShop))
                        {
                            FccShop shop = _fccShops.GetRow(arg);
                            AddFccShop(shop, npcBase, resident);
                            continue;
                        }

                        if (arg < FirstSpecialShopId || arg > LastSpecialShopId)
                        {
                            continue;
                        }

                        SpecialShopCustom specialShop = _specialShops.GetRow(arg);
                        AddSpecialItem(specialShop, npcBase, resident);
                    }
                }
            }
        }

        private void AddSpecialItem(SpecialShopCustom specialShop, ENpcBase npcBase, ENpcResident resident, ItemType type = ItemType.SpecialShop, string shop = null)
        {
            if (specialShop == null)
            {
                return;
            }

            foreach (SpecialShopCustom.Entry entry in specialShop.Entries)
            {
                if (entry.Result == null || entry.Cost == null)
                {
                    continue;
                }

                foreach (SpecialShopCustom.ResultEntry result in entry.Result)
                {
                    if (result.Item.Value == null)
                    {
                        continue;
                    }

                    if (result.Item.Value.Name == string.Empty)
                    {
                        continue;
                    }

                    List<Tuple<uint, string>> costs = (from e in entry.Cost where e.Item != null && e.Item.Value.Name != string.Empty select new Tuple<uint, string>(e.Count, e.Item.Value.Name)).ToList();

                    string achievementDescription = "";
                    if (type == ItemType.Achievement)
                    {
                        achievementDescription = _achievements.Where(i => i.Item.Value == result.Item.Value).Select(i => i.Description).First();
                    }

                    AddItem_Internal(result.Item.Value.RowId, result.Item.Value.Name, npcBase.RowId, resident.Singular, shop,
                        costs, _npcLocations.TryGetValue(npcBase.RowId, out NpcLocation value) ? value : null, type, achievementDescription);
                }
            }
        }

        private void AddGilShopItem(GilShop gilShop, ENpcBase npcBase, ENpcResident resident, string shop = null)
        {
            if (gilShop == null)
            {
                return;
            }

            for (uint i = 0u; ; i++)
            {
                try
                {
                    GilShopItem item = _gilShopItems.GetRow(gilShop.RowId, i);

                    if (item?.Item.Value == null)
                    {
                        break;
                    }


                    AddItem_Internal(item.Item.Value.RowId, item.Item.Value.Name, npcBase.RowId, resident.Singular,
                        shop != null ? $"{shop}\n{gilShop.Name}" : gilShop.Name,
                        new List<Tuple<uint, string>> { new(item.Item.Value.PriceMid, _gil.Name) },
                        _npcLocations.TryGetValue(npcBase.RowId, out NpcLocation value) ? value : null, ItemType.GilShop);
                }
                catch (Exception)
                {
                    break;
                }
            }
        }

        private void AddGcShopItem(GCShop gcId, ENpcBase npcBase, ENpcResident resident)
        {
            if (gcId == null)
            {
                return;
            }

            List<GCScripShopCategory> categories = _gcScripShopCategories.Where(i => i.GrandCompany.Row == gcId.GrandCompany.Row).ToList();
            if (categories.Count == 0)
            {
                return;
            }

            Item seal = _gcSeal.Find(i => i.Description.RawString.EndsWith($"{gcId.GrandCompany.Value.Name.RawString}."));
            if (seal == null)
            {
                return;
            }

            foreach (GCScripShopCategory category in categories)
            {
                for (uint i = 0u; ; i++)
                {
                    try
                    {
                        GCScripShopItem item = _gcScripShopItems.GetRow(category.RowId, i);
                        if (item == null)
                        {
                            break;
                        }

                        if (item.SortKey == 0)
                        {
                            break;
                        }

                        AddItem_Internal(item.Item.Value.RowId, item.Item.Value.Name, npcBase.RowId, resident.Singular, null, new List<Tuple<uint, string>> { new(item.CostGCSeals, seal.Name) },
                            _npcLocations.TryGetValue(npcBase.RowId, out NpcLocation value) ? value : null, ItemType.GcShop);
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }
            }
        }

        private void AddInclusionShop(InclusionShop inclusionShop, ENpcBase npcBase, ENpcResident resident)
        {
            if (inclusionShop == null)
            {
                return;
            }

            foreach (LazyRow<InclusionShopCategory> category in inclusionShop.Category)
            {
                if (category.Value.RowId == 0)
                {
                    continue;
                }

                for (uint i = 0; ; i++)
                {
                    try
                    {
                        InclusionShopSeriesCustom series = _inclusionShopSeries.GetRow(category.Value.InclusionShopSeries.Row, i);
                        if (series == null)
                        {
                            break;
                        }

                        SpecialShopCustom specialShop = series.SpecialShopCustoms.Value;
                        AddSpecialItem(specialShop, npcBase, resident, shop: $"{category.Value.Name}\n{specialShop?.Name}");
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }
            }
        }

        private void AddFccShop(FccShop shop, ENpcBase npcBase, ENpcResident resident)
        {
            if (shop == null)
            {
                return;
            }

            for (int i = 0; i < shop.Item.Length; i++)
            {
                Item item = _items.GetRow(shop.Item[i]);
                if (item == null || item.Name == string.Empty)
                {
                    continue;
                }

                uint cost = shop.Cost[i];

                AddItem_Internal(item.RowId, item.Name, npcBase.RowId, resident.Singular, null, new List<Tuple<uint, string>> { new(cost, _fccName.Text) },
                    _npcLocations.TryGetValue(npcBase.RowId, out NpcLocation value) ? value : null, ItemType.FcShop);
            }
        }

        private void AddItemsInPrehandler(PreHandler preHandler, ENpcBase npcBase, ENpcResident resident)
        {
            if (preHandler == null)
            {
                return;
            }

            uint target = preHandler.Target;
            if (target == 0)
            {
                return;
            }

            if (MatchEventHandlerType(target, EventHandlerType.GilShop))
            {
                GilShop gilShop = _gilShops.GetRow(target);
                AddGilShopItem(gilShop, npcBase, resident);
                return;
            }

            if (MatchEventHandlerType(target, EventHandlerType.SpecialShop))
            {
                SpecialShopCustom specialShop = _specialShops.GetRow(target);
                AddSpecialItem(specialShop, npcBase, resident);
                return;
            }

            InclusionShop inclusionShop = _inclusionShops.GetRow(target);
            AddInclusionShop(inclusionShop, npcBase, resident);
        }

        private void AddItemsInTopicSelect(TopicSelect topicSelect, ENpcBase npcBase, ENpcResident resident)
        {
            if (topicSelect == null)
            {
                return;
            }

            foreach (uint data in topicSelect.Shop)
            {
                if (data == 0)
                {
                    continue;
                }

                if (MatchEventHandlerType(data, EventHandlerType.SpecialShop))
                {
                    SpecialShopCustom specialShop = _specialShops.GetRow(data);
                    AddSpecialItem(specialShop, npcBase, resident, shop: topicSelect.Name);

                    continue;
                }

                if (MatchEventHandlerType(data, EventHandlerType.GilShop))
                {
                    GilShop gilShop = _gilShops.GetRow(data);
                    AddGilShopItem(gilShop, npcBase, resident, shop: topicSelect.Name);
                    continue;
                }

                PreHandler preHandler = _preHandlers.GetRow(data);
                AddItemsInPrehandler(preHandler, npcBase, resident);
            }
        }

        private void AddCollectablesShop(CollectablesShop shop, ENpcBase npcBase, ENpcResident resident)
        {
            if (shop == null)
            {
                return;
            }

            // skip rows without name
            if (shop.Name.RawString == string.Empty)
            {
                return;
            }

            for (uint i = 0; i < shop.ShopItems.Length; i++)
            {
                uint row = shop.ShopItems[i].Value.RowId;

                // 0 is unspecified, we dont need that
                if (row == 0)
                {
                    continue;
                }

                // 100 should be enough.. unless SE add more subrows in the future
                for (uint subRow = 0; subRow < 100; subRow++)
                {
                    try
                    {
                        CollectablesShopItem item = _collectablesShopItems.GetRow(row, subRow);
                        // filter out junk data
                        if (item.Item.Value == null || item.Item.Row <= 1000)
                        {
                            continue;
                        }

                        PluginLog.Warning($"{row}, {subRow} / {item.Item.Value.Name}, {item.Item.Row}");

                        AddItem_Internal(item.Item.Value.RowId, item.Item.Value.Name.RawString, npcBase.RowId,
                            resident.Singular.RawString, shop.ShopItems[i].Value.CollectablesShopItemGroup?.Value?.Name,
                            new List<Tuple<uint, string>>(), /* Will build cost later*/
                            _npcLocations.TryGetValue(npcBase.RowId, out NpcLocation value) ? value : null,
                            ItemType.SpecialShop /*Yes this is special shop*/);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }

        private bool HackyFix_Npc(ENpcBase npcBase, ENpcResident resident)
        {
            switch (npcBase.RowId)
            {
                case 1018655: // disreputable priest
                    AddSpecialItem(_specialShops.GetRow(1769743), npcBase, resident);
                    AddSpecialItem(_specialShops.GetRow(1769744), npcBase, resident);
                    AddSpecialItem(_specialShops.GetRow(1770537), npcBase, resident);
                    return true;

                case 1016289: // syndony
                    AddSpecialItem(_specialShops.GetRow(1769635), npcBase, resident);
                    return true;

                case 1025047: // gerolt but in eureka
                    for (uint i = 1769820; i <= 1769834; i++)
                    {
                        SpecialShopCustom specialShop = _specialShops.GetRow(i);
                        AddSpecialItem(specialShop, npcBase, resident);
                    }

                    return true;

                case 1025763: // doman junkmonger
                    AddGilShopItem(_gilShops.GetRow(262919), npcBase, resident);
                    return true;

                case 1027123: // eureka expedition artisan
                    AddSpecialItem(_specialShops.GetRow(1769934), npcBase, resident);
                    AddSpecialItem(_specialShops.GetRow(1769935), npcBase, resident);
                    return true;

                case 1027124: // eureka expedition scholar
                    AddSpecialItem(_specialShops.GetRow(1769937), npcBase, resident);
                    return true;

                case 1033921: // faux
                    AddSpecialItem(_specialShops.GetRow(1770282), npcBase, resident);
                    return true;

                case 1034007: // bozja
                case 1036895:
                    AddSpecialItem(_specialShops.GetRow(1770087), npcBase, resident);
                    return true;

                case 1027566: // Limbeth, Resplendent Tool Exchange
                    // we only need the first three npc data (the last one is CustomTalk, we dont need it here)
                    // the first one is from CollectablesShopItem and the last one is from SpecialShop
                    AddCollectablesShop(_collectablesShops.GetRow(npcBase.ENpcData[0]), npcBase, resident);
                    // the second one is obsolete materials, even though they are not craftable, still add them anyway
                    AddCollectablesShop(_collectablesShops.GetRow(npcBase.ENpcData[1]), npcBase, resident);
                    // after adding all the items, build the cost
                    AddSpecialItem(_specialShops.GetRow(npcBase.ENpcData[2]), npcBase, resident);

                    return true;

                case 1035014: // Spanner, Skysteel Tool Exchange (but it doesnt seem to do anything???)
                    // NPCData:
                    // 0 - Story
                    // 1 - Default talk
                    // 2 - CollectableShop
                    // 3 ~ 5 - PreHandler
                    AddCollectablesShop(_collectablesShops.GetRow(npcBase.ENpcData[2]), npcBase, resident);

                    for (int i = 3; i <= 5; i++)
                    {
                        PreHandler preHandler = _preHandlers.GetRow(npcBase.ENpcData[i]);
                        AddItemsInPrehandler(preHandler, npcBase, resident);
                    }

                    return true;

                case 1032900:
                    // NPCData:
                    // 0 - Story id
                    // 1 - SwitchTalk
                    // 2 ~ 3 SpecialShop 
                    // 4 - CollectableShop
                    // 5 - SpecialShop
                    // 6 - GilShop
                    // 7 - 8 PreHandler (Replica)

                    AddCollectablesShop(_collectablesShops.GetRow(npcBase.ENpcData[4]), npcBase, resident);

                    AddSpecialItem(_specialShops.GetRow(npcBase.ENpcData[2]), npcBase, resident);
                    AddSpecialItem(_specialShops.GetRow(npcBase.ENpcData[3]), npcBase, resident);
                    AddSpecialItem(_specialShops.GetRow(npcBase.ENpcData[5]), npcBase, resident);

                    AddGilShopItem(_gilShops.GetRow(npcBase.ENpcData[6]), npcBase, resident);
                    for (int i = 7; i <= 8; i++)
                    {
                        PreHandler preHandler = _preHandlers.GetRow(npcBase.ENpcData[i]);
                        AddItemsInPrehandler(preHandler, npcBase, resident);
                    }

                    return true;

                // TODO: Add quest reward
                case 1035012:


                    return true;

                default:
                    if (_shbFateShopNpc.TryGetValue(npcBase.RowId, out uint value))
                    {
                        AddSpecialItem(_specialShops.GetRow(value), npcBase, resident);
                        return true;
                    }

                    return false;
            }
        }

        private void AddAchievementItem()
        {
            for (uint i = 1006004u; i <= 1006006; i++)
            {
                ENpcBase npcBase = _eNpcBases.GetRow(i);
                ENpcResident resident = _eNpcResidents.GetRow(i);

                for (uint j = 1769898u; j <= 1769906; j++)
                {
                    AddSpecialItem(_specialShops.GetRow(j), npcBase, resident, ItemType.Achievement);
                }
            }
        }

        private void AddItem_Internal(uint itemId, string itemName, uint npcId, string npcName, string shopName, List<Tuple<uint, string>> cost, NpcLocation npcLocation, ItemType type,
            string achievementDesc = "")
        {
            if (itemId == 0)
            {
                return;
            }

            if (!_itemDataMap.ContainsKey(itemId))
            {
                _itemDataMap.Add(itemId, new ItemInfo
                {
                    Id = itemId,
                    Name = itemName,
                    NpcInfos = new List<NpcInfo> { new() { Id = npcId, Location = npcLocation, Costs = cost, Name = npcName, ShopName = shopName } },
                    Type = type,
                    AchievementDescription = achievementDesc,
                });
                return;
            }

            if (!_itemDataMap.TryGetValue(itemId, out ItemInfo itemInfo))
            {
                _ = _itemDataMap.TryAdd(itemId, itemInfo = new ItemInfo
                {
                    Id = itemId,
                    Name = itemName,
                    NpcInfos = new List<NpcInfo> { new() { Id = npcId, Location = npcLocation, Costs = cost, Name = npcName } },
                    Type = type,
                    AchievementDescription = achievementDesc,
                });
            }

            if (type == ItemType.Achievement && itemInfo.Type != ItemType.Achievement)
            {
                itemInfo.Type = ItemType.Achievement;
                itemInfo.AchievementDescription = achievementDesc;
            }

            List<NpcInfo> npcs = itemInfo.NpcInfos;
            if (npcs.Find(j => j.Id == npcId) == null)
            {
                npcs.Add(new NpcInfo { Id = npcId, Location = npcLocation, Name = npcName, Costs = cost, ShopName = shopName });
            }

            itemInfo.NpcInfos = npcs;
        }

        // https://github.com/ufx/GarlandTools/blob/3b3475bca6f95c800d2454f2c09a3f1eea0a8e4e/Garland.Data/Modules/Territories.cs
        private void BuildNpcLocation()
        {
            foreach (TerritoryType sTerritoryType in _territoryType)
            {
                string bg = sTerritoryType.Bg.ToString();
                if (string.IsNullOrEmpty(bg))
                {
                    continue;
                }

                string lgbFileName = "bg/" + bg[..(bg.IndexOf("/level/", StringComparison.Ordinal) + 1)] + "level/planevent.lgb";
                LgbFile sLgbFile = Service.DataManager.GetFile<LgbFile>(lgbFileName);
                if (sLgbFile == null)
                {
                    continue;
                }

                foreach (LayerCommon.Layer sLgbGroup in sLgbFile.Layers)
                {
                    foreach (LayerCommon.InstanceObject instanceObject in sLgbGroup.InstanceObjects)
                    {
                        if (instanceObject.AssetType != LayerEntryType.EventNPC)
                        {
                            continue;
                        }

                        LayerCommon.ENPCInstanceObject eventNpc = (LayerCommon.ENPCInstanceObject)instanceObject.Object;
                        uint npcRowId = eventNpc.ParentData.ParentData.BaseId;
                        if (npcRowId == 0)
                        {
                            continue;
                        }

                        if (_npcLocations.ContainsKey(npcRowId))
                        {
                            continue;
                        }

                        _npcLocations.Add(npcRowId, new NpcLocation(instanceObject.Transform.Translation.X, instanceObject.Transform.Translation.Z, sTerritoryType));
                    }
                }
            }

            ExcelSheet<Level> levels = Service.DataManager.GetExcelSheet<Level>();
            foreach (Level level in levels)
            {
                // NPC
                if (level.Type != 8)
                {
                    continue;
                }

                // NPC Id
                if (_npcLocations.ContainsKey(level.Object))
                {
                    continue;
                }

                if (level.Territory.Value == null)
                {
                    continue;
                }

                _npcLocations.Add(level.Object, new NpcLocation(level.X, level.Z, level.Territory.Value));
            }

            // https://github.com/ufx/GarlandTools/blob/7b38def8cf0ab553a2c3679aec86480c0e4e9481/Garland.Data/Modules/NPCs.cs#L59-L66
            TerritoryType corrected = _territoryType.GetRow(698);
            _npcLocations[1004418].TerritoryExcel = corrected;
            _npcLocations[1006747].TerritoryExcel = corrected;
            _npcLocations[1002299].TerritoryExcel = corrected;
            _npcLocations[1002281].TerritoryExcel = corrected;
            _npcLocations[1001766].TerritoryExcel = corrected;
            _npcLocations[1001945].TerritoryExcel = corrected;
            _npcLocations[1001821].TerritoryExcel = corrected;

            ManualItemCorrections.ApplyCorrections(_npcLocations);
        }

        public ItemInfo GetItemInfo(uint itemId)
        {
            return !_isDataReady ? null : _itemDataMap.TryGetValue(itemId, out ItemInfo itemInfo) ? itemInfo : null;
        }

        // https://github.com/SapphireServer/Sapphire/blob/a5c15f321f7e795ed7362ae15edaada99ca7d9be/src/world/Event/EventHandler.h#L48-L83
        internal enum EventHandlerType : uint
        {
            GilShop = 0x0004,
            CustomTalk = 0x000B,
            GcShop = 0x0016,
            SpecialShop = 0x001B,
            FcShop = 0x002A,    // not sure how these numbers were obtained by the folks at sapphire. This works for my isolated use case though I guess.
        }
    }
}