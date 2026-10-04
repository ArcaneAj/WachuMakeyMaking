using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Utils;

namespace WachuMakeyMaking.Services
{
    public class InventoryService : BaseService
    {
        private const int CHECK_PERIOD_SECONDS = 1;
        private readonly ExcelSheet<Item> itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        private ModItemStack[] inventory = [];
        private ModItemStack[] saddleBag = [];
        private Dictionary<string, ModItemStack[]> retainerCache = [];
        private List<ModItemStack> manualIngredients = [];
        private Dictionary<uint, int> manualQuantities = [];
        private Dictionary<string, ModItemStack[]> itemsBySourceCache = [];
        public Dictionary<string, bool> ItemSources { get; private set; } = [];
        private Timer? retainerTimer;

        public void Clear()
        {
            this.inventory = [];
            this.saddleBag = [];
            this.retainerCache = [];
            this.itemsBySourceCache = [];
            this.retainerTimer?.Dispose();
            this.retainerTimer = null;
        }

        public void ResetManualOverrides()
        {
            this.manualIngredients = [];
            this.manualQuantities = [];
        }

        public void Init()
        {
            this.inventory = GetInventory();
            this.saddleBag = GetSaddleBag();
            this.retainerTimer?.Dispose();
            this.retainerTimer = new Timer(_ =>
            {
                try
                {
                    CheckActiveRetainer();
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"CheckActiveRetainer timer error: {ex}");
                }
            }, null, 0, TimeSpan.FromSeconds(CHECK_PERIOD_SECONDS).Milliseconds);

            this.ItemSources = this.GetPopulatedItemSources().ToDictionary(x => x, x => true);
            this.EmitInitCompleteEvent();
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
            return [
                ..GetItemsFromInventory(GameInventoryType.Crystals),
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

        public ModItemStack[] GetOwnedItems()
        {
            var itemSourceFilters = this.ItemSources;
            //Plugin.Log.Info($"GetOwnedItems called with filters: {string.Join(", ", itemSourceFilters.Select(kvp => $"{kvp.Key}: {kvp.Value}"))}");

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
            var manualItems = GetOwnedItems().Concat(this.manualIngredients).Select(x => new ModItemStack(x.Item, x.Id, this.manualQuantities.GetValueOrDefault(x.Id, x.Quantity)));
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

        private ModItemStack[] GetItemsFromInventory(GameInventoryType inventory)
        {
            return Plugin.GameInventory.GetInventoryItems(inventory).ToArray()
                .Where(x => x.ItemId != 0)
                .SelectMany(i => this.itemSheet.TryGetRow(i.BaseItemId, out var row) ? [new ModItemStack(row.ToMod(), i.BaseItemId, i.Quantity)] : Array.Empty<ModItemStack>())
                .ToArray();
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
