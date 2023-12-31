﻿using System;
using System.Collections.Generic;
using System.Linq;
using CheapLoc;
using Dalamud.ContextMenu;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ItemVendorLocation.Models;
using Lumina.Excel;
using Lumina.Excel.GeneratedSheets;
using XivCommon;
using XivCommon.Functions.Tooltips;
using GrandCompany = FFXIVClientStructs.FFXIV.Client.UI.Agent.GrandCompany;
using AgentInterface = FFXIVClientStructs.FFXIV.Component.GUI.AgentInterface;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Keys;
using ImGuiNET;

namespace ItemVendorLocation
{
    public class EntryPoint : IDalamudPlugin
    {
        private static readonly List<string> GameAddonWhitelist = new()
        {
            "ChatLog",
            "ColorantColoring",
            "ContentsInfoDetail",
            "DailyQuestSupply",
            "HousingCatalogPreview",
            "HousingGoods",
            "ItemSearch",
            "Journal",
            "RecipeMaterialList",
            "RecipeNote",
            "RecipeTree",
            "ShopExchangeItem",
            "ShopExchangeItemDialog",
            "SubmarinePartsMenu",
            "Tryon",
        };

        public readonly Dictionary<byte, uint> GcVendorIdMap = new()
        {
            { 0, 0 },
            { 1, 1002387 },
            { 2, 1002393 },
            { 3, 1002390 },
        };

        public readonly Dictionary<GrandCompany, uint> OicVendorIdMap = new()
        {
            { GrandCompany.Maelstrom, 1002389 },
            { GrandCompany.TwinAdder, 1000165 },
            { GrandCompany.ImmortalFlames, 1003925 },
            { GrandCompany.None, 0 },
        };

        private readonly string _buttonName;
#if DEBUG
        public readonly ItemLookup _itemLookup;
#else
        private readonly ItemLookup _itemLookup;
#endif
        private readonly WindowSystem _windowSystem;
        private readonly XivCommonBase _xivCommon;

        private readonly ExcelSheet<Item> _items;

        public EntryPoint([RequiredVersion("1.0")] DalamudPluginInterface pi)
        {
            _ = pi.Create<Service>();

            Localization.SetupLocalization(Service.ClientState.ClientLanguage);
            _buttonName = Loc.Localize("ContextMenuItem", "Vendor location");
            _itemLookup = new();
            Service.Plugin = this;
            Service.Configuration = pi.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
            Service.ContextMenu = new DalamudContextMenu(pi);
            Service.Ipc = new Ipc(pi);
            Service.Ipc.Enable();
            _xivCommon = new(pi, Hooks.Tooltips);

            // Initialize the UI
            _windowSystem = new WindowSystem(typeof(EntryPoint).AssemblyQualifiedName);
            Service.SettingsUi = new();
            Service.PluginUi = new();

            _items = Service.DataManager.GetExcelSheet<Item>();

            _windowSystem.AddWindow(Service.PluginUi);
            _windowSystem.AddWindow(Service.SettingsUi);

            Service.Ipc.OnOpenChatTwoItemContextMenu += OnOpenChatTwoItemContextMenu;
            _xivCommon.Functions.Tooltips.OnItemTooltip += Tooltips_OnOnItemTooltip;
            Service.ContextMenu.OnOpenInventoryContextMenu += ContextMenu_OnOpenInventoryContextMenu;
            Service.ContextMenu.OnOpenGameObjectContextMenu += ContextMenu_OnOpenGameObjectContextMenu;
            Service.Interface.UiBuilder.Draw += _windowSystem.Draw;
            Service.Interface.UiBuilder.OpenConfigUi += OnOpenConfigUi;
            _ = Service.CommandManager.AddHandler(Service.Configuration.CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Displays the Item Vendor Location config window",
            });
        }

        private void OnCommand(string command, string args)
        {
            if (args.IsNullOrEmpty())
            {
                Service.SettingsUi.IsOpen = true;
                return;
            }

            _ = Task.Run(() =>
            {
                if (_items.Any(i => string.Equals(i.Name.RawString, args, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (ItemInfo itemDetails in _items.Where(i => string.Equals(i.Name.RawString, args, StringComparison.OrdinalIgnoreCase))
                                                           .Select(item => _itemLookup.GetItemInfo(item.RowId)).Where(itemDetails => itemDetails != null))
                    {
                        ShowSingleVendor(itemDetails);
                    }

                    return;
                }

                List<Item> items = _items.Where(i => i.Name.RawString.ToLower().Contains(args.ToLower())).ToList();
                if (items.Count == 0)
                {
                    Utilities.OutputChatLine($" No items found for \"{args}\"");
                    return;
                }

                if (items.Count > 20)
                {
                    Utilities.OutputChatLine("You may want to refine your search");
                }

                uint results = 0;
                foreach (Item item in items)
                {
                    if (results == Service.Configuration.MaxSearchResults)
                    {
                        Utilities.OutputChatLine($"Displayed {Service.Configuration.MaxSearchResults}/{items.Count} matches.");
                        if (items.Count > Service.Configuration.MaxSearchResults)
                        {
                            Utilities.OutputChatLine("You may want to be more specific.");
                        }

                        break;
                    }

                    ItemInfo itemDetails = _itemLookup.GetItemInfo(item.RowId);
                    if (itemDetails == null)
                    {
                        continue;
                    }

                    results++;
                    ShowSingleVendor(itemDetails);
                }

                if (results == 0)
                {
                    Utilities.OutputChatLine($"No vendors found for \"{args}\"");
                }
            });
        }

        private void OnOpenConfigUi()
        {
            Service.SettingsUi.IsOpen = true;
        }

        public string Name => "ItemVendorLocation";

        private static uint CorrectitemId(uint itemId)
        {
            return itemId switch
            {
                > 1000000 => itemId - 1000000, // hq
                /*
                 > 500000 and < 1000000 => itemId - 500000, // collectible, doesnt seem to work
                 */
                _ => itemId
            };
        }

        private void OnOpenGameObjectContextMenu(GameObjectContextMenuOpenArgs args, uint itemId)
        {
            ItemInfo itemInfo = _itemLookup.GetItemInfo(itemId);
            if (itemInfo == null)
            {
                return;
            }

            args.AddCustomItem(new GameObjectContextMenuItem(_buttonName, _ => { ContextMenuCallback(itemInfo); }, true));
        }

        private void OnOpenChatTwoItemContextMenu(uint itemId)
        {
            ItemInfo itemInfo = _itemLookup.GetItemInfo(itemId);
            if (itemInfo == null)
            {
                return;
            }

            if (ImGui.Selectable(_buttonName))
            {
                ContextMenuCallback(itemInfo);
            }
        }

        private unsafe void ContextMenu_OnOpenGameObjectContextMenu(GameObjectContextMenuOpenArgs args)
        {
            // I think players have a world and items never will??
            // Hopefully this removes the vendor menu for players in the chat log
            if (args.ObjectWorld != 0)
            {
                return;
            }

            if (!GameAddonWhitelist.Contains(args.ParentAddonName))
            {
                return;
            }

            // thank you ottermandias for the offsets
            uint itemId;
            switch (args.ParentAddonName)
            {
                case "RecipeNote":
                {
                    nint recipeNoteAgent = Service.GameGui.FindAgentInterface(args.ParentAddonName);
                    itemId = *(uint*)(recipeNoteAgent + 0x398);
                    break;
                }
                case "RecipeTree" or "RecipeMaterialList":
                {
                    UIModule* uiModule = (UIModule*)Service.GameGui.GetUIModule();
                    AgentModule* agents = uiModule->GetAgentModule();
                    AgentInterface* agent = agents->GetAgentByInternalId(AgentId.RecipeItemContext);

                    itemId = *(uint*)((nint)agent + 0x28);
                    break;
                }
                case "ColorantColoring":
                {
                    nint colorantColoringAgent = Service.GameGui.FindAgentInterface(args.ParentAddonName);
                    itemId = *(uint*)(colorantColoringAgent + 0x34);
                    break;
                }
                default:
                    itemId = CorrectitemId((uint)Service.GameGui.HoveredItem);
                    break;
            }

            OnOpenGameObjectContextMenu(args, itemId);
        }

        private void ContextMenu_OnOpenInventoryContextMenu(InventoryContextMenuOpenArgs args)
        {
            uint itemId = CorrectitemId(args.ItemId);
            ItemInfo itemInfo = _itemLookup.GetItemInfo(itemId);
            if (itemInfo == null)
            {
                return;
            }

            args.AddCustomItem(new InventoryContextMenuItem(_buttonName, _ => { ContextMenuCallback(itemInfo); }, true));
        }

        private unsafe void Tooltips_OnOnItemTooltip(ItemTooltip itemtooltip, ulong itemid)
        {
            ItemInfo itemInfo = _itemLookup.GetItemInfo(CorrectitemId((uint)itemid));
            if (itemInfo == null)
            {
                return;
            }

            SeString origStr = itemtooltip[ItemTooltipString.ShopSellingPrice];
            int colonIndex = origStr.TextValue.IndexOfAny(new[] { '：', ':' });

            switch (itemInfo.Type)
            {
                case ItemType.GcShop:
                    List<NpcInfo> npcInfos = itemInfo.NpcInfos;
                    byte playerGC = UIState.Instance()->PlayerState.GrandCompany;
                    IEnumerable<uint> otherGcVendorIds = Service.Plugin.GcVendorIdMap.Values.Where(i => i != Service.Plugin.GcVendorIdMap[playerGC]);
                    // Only remove items if doing so doesn't remove all the results
                    if (npcInfos.Any(i => !otherGcVendorIds.Contains(i.Id)))
                    {
                        _ = npcInfos.RemoveAll(i => otherGcVendorIds.Contains(i.Id));
                    }

                    NpcInfo info = npcInfos.First();

                    string costStr = $"{info.Costs[0].Item2} x{info.Costs[0].Item1}";

                    itemtooltip[ItemTooltipString.ShopSellingPrice] = string.Concat(origStr.TextValue.AsSpan(0, colonIndex), ": ", costStr);
                    return;
                case ItemType.SpecialShop:
                    // Avoid modification for certain seasonal items with no Shop Selling Price line
                    if (colonIndex != -1)
                    {
                        itemtooltip[ItemTooltipString.ShopSellingPrice] = string.Concat(origStr.TextValue.AsSpan(0, colonIndex), ": Special Vendor");
                    }
                    return;
                case ItemType.FcShop:
                    info = itemInfo.NpcInfos.First();
                    costStr = $"FC Credits x{info.Costs[0].Item1}";
                    itemtooltip[ItemTooltipString.ShopSellingPrice] = string.Concat(origStr.TextValue.AsSpan(0, colonIndex), ": ", costStr);
                    return;
                case ItemType.CollectableExchange:
                    itemtooltip[ItemTooltipString.ShopSellingPrice] =
                        string.Concat(origStr.TextValue.AsSpan(0, colonIndex), ": Collectables Exchange Reward");
                    return;
            }
        }

        private void ContextMenuCallback(ItemInfo itemInfo)
        {
            // filteredResults allows us to apply filters without modifying core data,
            // itemInfo is initialized once upon plugin load, so a filter would not
            // be able to be unchecked otherwise
            ItemInfo filteredResults = new()
            {
                AchievementDescription = itemInfo.AchievementDescription,
                Id = itemInfo.Id,
                Name = itemInfo.Name,
                Type = itemInfo.Type,
                NpcInfos = itemInfo.NpcInfos,
            };

            filteredResults.ApplyFilters();

            ResultDisplayHandler(filteredResults);
        }

        private static void ShowMultipleVendors(ItemInfo item)
        {
            Service.PluginUi.SetItemToDisplay(item);
            Service.PluginUi.IsOpen = true;
            Service.PluginUi.Collapsed = false;
            Service.PluginUi.CollapsedCondition = ImGuiCond.Once;
        }

        private static void ShowSingleVendor(ItemInfo item)
        {
            NpcInfo vendor;
            SeStringBuilder sb = new();
            try
            {
                vendor = item.NpcInfos.First(i => i.Location != null);
            }
            catch (InvalidOperationException)
            {
                _ = sb.AddText("No NPCs with a location could be found for ");
                _ = sb.Append(SeString.CreateItemLink(item.Id, false));
                Utilities.OutputChatLine(sb.BuiltString);
                return;
            }

            _ = sb.Append(SeString.CreateItemLink(item.Id, false));
            _ = sb.AddText(" can be purchased from ");
            _ = sb.AddUiForeground(Service.Configuration.NPCNameChatColor);
            _ = sb.AddText(vendor.Name);
            _ = sb.AddUiForegroundOff();
            _ = sb.AddText(" at ");
            _ = sb.Append(SeString.CreateMapLink(vendor.Location.TerritoryType, vendor.Location.MapId, vendor.Location.MapX, vendor.Location.MapY));
            Utilities.OutputChatLine(sb.BuiltString);
        }

        private static void ResultDisplayHandler(ItemInfo item)
        {
            ResultsViewType viewType = Service.Configuration.ResultsViewType;
            if (Service.Configuration.SearchDisplayModifier != VirtualKey.NO_KEY && Service.KeyState.GetRawValue(Service.Configuration.SearchDisplayModifier) == 1)
            {
                viewType = viewType == ResultsViewType.Single ? ResultsViewType.Multiple : ResultsViewType.Single;
            }

            if (viewType == ResultsViewType.Multiple)
            {
                ShowMultipleVendors(item);
            }
            else
            {
                ShowSingleVendor(item);
            }
        }

        #region IDisposable Support

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            Service.Ipc.OnOpenChatTwoItemContextMenu -= OnOpenChatTwoItemContextMenu;
            Service.Ipc.Disable();

            _ = Service.CommandManager.RemoveHandler(Service.Configuration.CommandName);
            _xivCommon.Functions.Tooltips.OnItemTooltip -= Tooltips_OnOnItemTooltip;
            Service.ContextMenu.OnOpenInventoryContextMenu -= ContextMenu_OnOpenInventoryContextMenu;
            Service.ContextMenu.OnOpenGameObjectContextMenu -= ContextMenu_OnOpenGameObjectContextMenu;
            Service.ContextMenu.Dispose();

            Service.Interface.UiBuilder.Draw -= _windowSystem.Draw;
            Service.Interface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
            _windowSystem.RemoveAllWindows();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}