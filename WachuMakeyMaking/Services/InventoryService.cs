using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Utils;

namespace WachuMakeyMaking.Services
{
    public class InventoryService
    {
        private readonly ExcelSheet<Item> itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        private ModItemStack[] inventory = [];
        private ModItemStack[] crystals = [];
        private ModItemStack[] saddleBag = [];
        private Dictionary<string, ModItemStack[]> retainerCache = [];

        private Dictionary<string, ModItemStack[]> itemsBySourceCache = [];

        public void Clear()
        {
            this.inventory = [];
            this.crystals = [];
            this.saddleBag = [];
            this.retainerCache = [];
            this.itemsBySourceCache = [];
        }

        public void Init()
        {
            this.inventory = GetInventory();
            this.crystals = GetCrystals();
            this.saddleBag = GetSaddleBag();
        }

        public void Reset()
        {
            Clear();
            Init();
        }

        private void CheckActiveRetainer()
        {
            // Try get retainer items if open
            // Cache them between openings so we remember what was in them even if they get closed
            unsafe
            {
                var retainerManager = RetainerManager.Instance();
                if (retainerManager == null || !retainerManager->IsReady)
                {
                    return;
                }

                var retainer = retainerManager->GetActiveRetainer();
                if (retainer->Available)
                {
                    // Retainer information fields available on the struct:
                    var name = retainer->NameString;
                    //var retainerId = retainer->RetainerId;
                    //uint ventureId = retainer.VentureId;
                    //uint ventureComplete = retainer.VentureComplete;
                    //byte classJob = retainer.ClassJob;
                    //byte level = retainer.Level;
                    //uint gil = retainer.Gil;
                    //byte marketItemCount = retainer.MarketItemCount;

                    this.retainerCache[name] = [
                        ..GetItemsFromInventory(GameInventoryType.RetainerCrystals),
                        ..GetItemsFromInventory(GameInventoryType.RetainerPage1),
                        ..GetItemsFromInventory(GameInventoryType.RetainerPage2),
                        ..GetItemsFromInventory(GameInventoryType.RetainerPage3),
                        ..GetItemsFromInventory(GameInventoryType.RetainerPage4),
                        ..GetItemsFromInventory(GameInventoryType.RetainerPage5),
                        ..GetItemsFromInventory(GameInventoryType.RetainerPage6),
                        ..GetItemsFromInventory(GameInventoryType.RetainerPage7)
                        ];
                }
            }
        }

        private ModItemStack[] GetSaddleBag()
        {
            return [
                ..GetItemsFromInventory(GameInventoryType.SaddleBag1),
                ..GetItemsFromInventory(GameInventoryType.SaddleBag2),
                ..GetItemsFromInventory(GameInventoryType.PremiumSaddleBag1),
                ..GetItemsFromInventory(GameInventoryType.PremiumSaddleBag2)
                ];
        }

        private ModItemStack[] GetInventory()
        {
            // Collect items from all inventory bags (excluding crystals)
            return [
                ..GetItemsFromInventory(GameInventoryType.Inventory1),
                ..GetItemsFromInventory(GameInventoryType.Inventory2),
                ..GetItemsFromInventory(GameInventoryType.Inventory3),
                ..GetItemsFromInventory(GameInventoryType.Inventory4)
                ];
        }

        public ModItemStack[] GetCrystals()
        {
            return [.. GetItemsFromInventory(GameInventoryType.Crystals)];
        }

        public ModItemStack[] GetConsolidatedItems(Dictionary<string, bool> itemSourceFilters)
        {
            Plugin.Log.Info($"GetConsolidatedItems called with filters: {string.Join(", ", itemSourceFilters.Select(kvp => $"{kvp.Key}: {kvp.Value}"))}");

            var itemBySource = GetItemsBySource();

            // Consolidate items with the same ID
            var consolidatedItems = itemBySource
                .Where(x => itemSourceFilters.ContainsKey(x.Key) && itemSourceFilters[x.Key])
                .SelectMany(x => x.Value)
                .GroupBy(stack => stack.Id)
                .Select(group =>
                {
                    var firstStack = group.First();
                    var totalQuantity = group.Sum(stack => stack.Quantity);
                    return new ModItemStack(firstStack.Item, firstStack.Id, totalQuantity);
                })
                .ToArray();

            return consolidatedItems;
        }

        public List<string> GetPopulatedItemSources()
        {
            if (itemsBySourceCache.Count > 0)
            {
                return [.. itemsBySourceCache.Where(x => x.Value.Length > 0).Select(x => x.Key).Union(["Inventory"])];
            }

            var itemBySource = GetItemsBySource();
            return [.. itemBySource.Where(x => x.Value.Length > 0).Select(x => x.Key).Union(["Inventory"])];
        }

        public Dictionary<string, ModItemStack[]> GetItemsBySource()
        {
            var itemBySource = new Dictionary<string, ModItemStack[]>
            {
                ["Inventory"] = this.inventory,
                ["SaddleBag"] = this.saddleBag,
            };

            foreach (var (k, v) in this.retainerCache)
            {
                itemBySource[k] = v;
            }

            this.itemsBySourceCache = itemBySource;

            return itemBySource;
        }

        private ModItemStack[] GetItemsFromInventory(GameInventoryType inventory)
        {
            return Plugin.GameInventory.GetInventoryItems(inventory).ToArray()
                .Where(x => x.ItemId != 0)
                .SelectMany(i => this.itemSheet.TryGetRow(i.BaseItemId, out var row) ? [new ModItemStack(row.ToMod(), i.BaseItemId, i.Quantity)] : Array.Empty<ModItemStack>())
                .ToArray();
        }
    }
}
