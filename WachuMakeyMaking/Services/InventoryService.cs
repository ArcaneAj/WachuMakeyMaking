using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Utils;

namespace WachuMakeyMaking.Services
{
    public class InventoryService : BaseService
    {
        private static readonly ExcelSheet<Item> ItemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        private ModItemStack[] inventory = [];
        private ModItemStack[] saddleBag = [];
        private Dictionary<string, ModItemStack[]> retainerCache = [];
        private List<ModItemStack> manualIngredients = [];
        private Dictionary<uint, int> manualQuantities = [];
        private Dictionary<string, ModItemStack[]> itemsBySourceCache = [];
        public Dictionary<string, bool> ItemSources { get; private set; } = [];

        public void Clear()
        {
            this.inventory = [];
            this.saddleBag = [];
            this.retainerCache = [];
            this.itemsBySourceCache = [];
            ResetManualOverrides();
        }

        public void ResetManualOverrides()
        {
            this.manualIngredients = [];
            this.manualQuantities = [];
        }

        public void Update(HashSet<GameInventoryType> inventoriesChanged, bool itemMoved)
        {
            this.itemsBySourceCache = [];
            if (itemMoved || inventoriesChanged.Any(RetainerTypes.Contains))
                CheckActiveRetainer();
            if (itemMoved || inventoriesChanged.Any(InventoryTypes.Contains))
                this.inventory = GetInventory();
            if (itemMoved || inventoriesChanged.Any(SaddleBagTypes.Contains))
                this.saddleBag = GetSaddleBag();
            this.ItemSources = this.GetPopulatedItemSources()
                .ToDictionary(x => x, x => this.ItemSources.GetValueOrDefault(x, true));
            this.EmitInitCompleteEvent();
        }

        public void Init()
        {
            this.inventory = GetInventory();
            this.saddleBag = GetSaddleBag();

            this.ItemSources = this.GetPopulatedItemSources().ToDictionary(x => x, x => true);
            this.EmitInitCompleteEvent();
        }

        private static readonly HashSet<GameInventoryType> RetainerTypes =
        [
            GameInventoryType.RetainerCrystals,
            GameInventoryType.RetainerPage1,
            GameInventoryType.RetainerPage2,
            GameInventoryType.RetainerPage3,
            GameInventoryType.RetainerPage4,
            GameInventoryType.RetainerPage5,
            GameInventoryType.RetainerPage6,
            GameInventoryType.RetainerPage7,
        ];

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

                    this.retainerCache[name] = [.. RetainerTypes.SelectMany(GetItemsFromInventory)];
                }
            }
        }

        private static readonly HashSet<GameInventoryType> SaddleBagTypes =
        [
            GameInventoryType.SaddleBag1,
            GameInventoryType.SaddleBag2,
            GameInventoryType.PremiumSaddleBag1,
            GameInventoryType.PremiumSaddleBag2,
        ];

        private static ModItemStack[] GetSaddleBag()
        {
            return [.. SaddleBagTypes.SelectMany(GetItemsFromInventory)];
        }

        private static readonly HashSet<GameInventoryType> InventoryTypes =
        [
            GameInventoryType.Crystals,
            GameInventoryType.Inventory1,
            GameInventoryType.Inventory2,
            GameInventoryType.Inventory3,
            GameInventoryType.Inventory4,
        ];

        private static ModItemStack[] GetInventory()
        {
            return [.. InventoryTypes.SelectMany(GetItemsFromInventory)];
        }

        public ModItemStack[] GetOwnedItems()
        {
            var itemSourceFilters = this.ItemSources;

            var itemsBySource = GetItemsBySource();

            // Consolidate items with the same ID
            var consolidatedItems = itemsBySource
                .Where(x => itemSourceFilters.ContainsKey(x.Key) && itemSourceFilters[x.Key])
                .SelectMany(x => x.Value)
                .GroupBy(stack => stack.Id)
                .Select(group =>
                {
                    var firstStack = group.First();
                    return new ModItemStack(firstStack.Item, firstStack.Id, group.Sum(stack => stack.Quantity));
                })
                .ToArray();

            return consolidatedItems;
        }

        public ModItemStack[] GetOverriddenItems()
        {
            var manualItems = GetOwnedItems()
                .Concat(this.manualIngredients)
                .Select(x => new ModItemStack(x.Item, x.Id, this.manualQuantities.GetValueOrDefault(x.Id, x.Quantity)));
            return [.. manualItems];
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

        private static List<ModItemStack> GetItemsFromInventory(GameInventoryType inventory)
        {
            var items = Plugin.GameInventory.GetInventoryItems(inventory);

            var result = new List<ModItemStack>(items.Length);
            for (var i = 0; i < items.Length; i++)
            {
                ref readonly var item = ref items[i];

                if (item.ItemId == 0)
                    continue;

                if (ItemSheet.TryGetRow(item.BaseItemId, out var row))
                {
                    result.Add(new ModItemStack(row.ToMod(), item.BaseItemId, item.Quantity));
                }
            }

            return result;
        }

        public void AddManualIngredient(ModItemStack modItemStack)
        {
            this.manualIngredients.Add(modItemStack);
        }

        internal void SetItemQuantity(uint itemId, int quantity)
        {
            this.manualQuantities[itemId] = quantity;
        }
    }
}
