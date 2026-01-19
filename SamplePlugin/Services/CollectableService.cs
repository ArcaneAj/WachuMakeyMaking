using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using SamplePlugin.Models;
using System.Collections.Generic;

namespace SamplePlugin.Services;

public class CollectableService
{
    private readonly Plugin plugin;
    private readonly Dictionary<Item, ModItemStack> collectablesCache;
    private readonly Dictionary<Item, ModItemStack> restorationCache;

    public CollectableService(Plugin plugin)
    {
        this.plugin = plugin;

        this.collectablesCache = new Dictionary<Item, ModItemStack>();
        this.restorationCache = new Dictionary<Item, ModItemStack>();

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

                    var currencyItem = itemSheet.GetRow(currency);

                    collectablesCache[shopItem.Item.Value] = new ModItemStack(currencyItem, currency, shopRewardScrip.HighReward);
                }
            }
        }

        var hWDCrafterSupply = Plugin.DataManager.GetExcelSheet<HWDCrafterSupply>();
        var hWDCrafterSupplyReward = Plugin.DataManager.GetExcelSheet<HWDCrafterSupplyReward>();
        var hWDCrafterSupplyTerm = Plugin.DataManager.GetExcelSheet<HWDCrafterSupplyTerm>();

        foreach (var handInType in hWDCrafterSupply)
        {
            foreach (var itemHandIn in handInType.HWDCrafterSupplyParams)
            {
                var scripId = itemHandIn.HighCollectableRewardPostPhase.RowId;
                var scrip = hWDCrafterSupplyReward.GetRow(scripId).ScriptRewardAmount;
                var item = itemSheet.GetRow(itemHandIn.ItemTradeIn.RowId);
                restorationCache[item] = new ModItemStack(item, scripId, scrip);
            }
        }
    }

    public enum ScripType
    {
        None,
        CraftersScrip,
        SkybuildersScrip
    }

    /// <summary>
    /// Get collectable information (scrip type and value) for a given result item.
    /// Tries to read real values from the SpecialShop Excel sheet, and falls back to
    /// name-based heuristics if no shop entry is found.
    /// </summary>
    public (bool isCollectable, Item currency, int scripValue) GetCollectableInfo(Item item)
    {
        if (this.collectablesCache.TryGetValue(item, out var crafterScrips))
        {
            return (true, crafterScrips.Item, crafterScrips.Quantity);
        }

        if (this.restorationCache.TryGetValue(item, out var skybuildersScrips))
        {
            return (true, skybuildersScrips.Item, skybuildersScrips.Quantity);
        }

        return (false, new Item(), 0);

    }
}
