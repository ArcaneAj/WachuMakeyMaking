using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Utils;

namespace WachuMakeyMaking.Services;

public class CollectableService
{
    private readonly Dictionary<ModItem, ModItemStack> collectablesCache = [];
    private readonly Dictionary<ModItem, ModItemStack> restorationCache = [];

    public CollectableService()
    {
        this.Init();
    }

    private void Init()
    {
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();

        var collectablesShops = Plugin.DataManager.GetExcelSheet<CollectablesShop>();
        var collectablesShopItems = Plugin.DataManager.GetSubrowExcelSheet<CollectablesShopItem>();
        var collectablesShopRewardScrip = Plugin.DataManager.GetExcelSheet<CollectablesShopRewardScrip>();

        foreach (var shop in collectablesShops)
        {
            if (shop.RowId != 3866626) continue;

            foreach (var row in shop.ShopItems)
            {
                var shopItems = collectablesShopItems.GetRow(row.RowId);
                foreach (var shopItem in shopItems)
                {
                    var shopRewardScrip = collectablesShopRewardScrip.GetRow(shopItem.CollectablesShopRewardScrip.RowId);
                    uint currency = 0;
                    unsafe
                    {
                        currency = CurrencyManager.Instance()->GetItemIdBySpecialId((byte)shopRewardScrip.Currency);
                    }

                    var currencyItem = itemSheet.GetRow(currency).ToMod();
                    var shopItemMod = itemSheet.GetRow(shopItem.Item.RowId).ToMod();

                    this.collectablesCache[shopItemMod] = new ModItemStack(currencyItem, currency, shopRewardScrip.HighReward);
                }
            }
        }

        var hWDCrafterSupply = Plugin.DataManager.GetExcelSheet<HWDCrafterSupply>();
        var hWDCrafterSupplyReward = Plugin.DataManager.GetExcelSheet<HWDCrafterSupplyReward>();
        var hWDCrafterSupplyTerm = Plugin.DataManager.GetExcelSheet<HWDCrafterSupplyTerm>();

        var skybuildersScrip = itemSheet.GetRow(28063).ToMod();

        foreach (var handInType in hWDCrafterSupply)
        {
            foreach (var itemHandIn in handInType.HWDCrafterSupplyParams)
            {
                var scripId = itemHandIn.HighCollectableRewardPostPhase.RowId;
                var scrip = hWDCrafterSupplyReward.GetRow(scripId).ScriptRewardAmount;
                var item = itemSheet.GetRow(itemHandIn.ItemTradeIn.RowId).ToMod();
                this.restorationCache[item] = new ModItemStack(skybuildersScrip, scripId, scrip);
            }
        }
    }

    /// <summary>
    /// Get collectable information (scrip type and value) for a given result item.
    /// Tries to read real values from the SpecialShop Excel sheet, and falls back to
    /// name-based heuristics if no shop entry is found.
    /// </summary>
    public (bool isCollectable, ModItem currency, int scripValue) GetCollectableInfo(ModItem item)
    {
        if (this.collectablesCache.TryGetValue(item, out var crafterScrips))
        {
            return (true, crafterScrips.Item, crafterScrips.Quantity);
        }

        if (this.restorationCache.TryGetValue(item, out var skybuildersScrips))
        {
            return (true, skybuildersScrips.Item, skybuildersScrips.Quantity);
        }

        return (false, new ModItem(0, ""), 0);

    }
}
