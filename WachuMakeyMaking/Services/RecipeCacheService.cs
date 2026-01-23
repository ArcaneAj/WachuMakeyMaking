using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Utils;

namespace WachuMakeyMaking.Services;

public class RecipeCacheService
{
    private readonly Plugin plugin;
    private readonly UniversalisService universalisService;
    private readonly CollectableService collectableService;

    // Cache for recipes to avoid recalculating every frame
    private List<ModRecipeWithValue> cachedRecipes = new();
    private bool isCacheInitializing = false;

    public RecipeCacheService(Plugin plugin, UniversalisService universalisService, CollectableService collectableService)
    {
        this.plugin = plugin;
        this.universalisService = universalisService;
        this.collectableService = collectableService;

    }

    public bool IsCacheInitializing => isCacheInitializing;

    public string CurrentProcessingStep { get; private set; } = string.Empty;

    public int CurrentProgress { get; private set; }

    public int TotalProgress { get; private set; }

    public List<ModRecipeWithValue> CachedRecipes => cachedRecipes;

    private CancellationTokenSource cancellationTokenSource = new();
    private ModItemStack[] items = [];
    private ModItemStack[] crystals = [];

    public void ForceRefresh(ModItemStack[] modItemStacks)
    {
        var crystalIds = GetCrystals().Select(x => x.Id).ToArray();
        items = [..modItemStacks.Where(x => !crystalIds.Contains(x.Id))];
        crystals = [.. modItemStacks.Where(x => crystalIds.Contains(x.Id))];
        cachedRecipes.Clear();
        isCacheInitializing = false;
        CurrentProcessingStep = string.Empty;
        CurrentProgress = 0;
        TotalProgress = 0;
        cancellationTokenSource.Cancel();
    }

    public async Task EnsureCacheInitializedAsync()
    {
        if (cachedRecipes.Count == 0 && !isCacheInitializing)
        {
            cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;
            isCacheInitializing = true;
            await InitializeRecipeCacheAsync(cancellationToken);
        }
    }

    private async Task InitializeRecipeCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            CurrentProcessingStep = "Getting inventory items...";
            CurrentProgress = 0;

            // Get consolidated item stacks and crystals

            var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
            var gil = itemSheet.GetRow(1).ToMod();

            // Create inventory count lookup (items + crystals)
            var inventoryCounts = items.ToDictionary(
                stack => stack.Id,
                stack => stack.Quantity
            );

            foreach (var crystal in crystals)
            {
                inventoryCounts[crystal.Id] = crystal.Quantity;
            }

            // Set total progress to the number of consolidated items to process
            TotalProgress = items.Length;
            CurrentProcessingStep = $"Finding recipes... (0/{items.Length} items)";

            // Find all recipes that use at least one ingredient from inventory
            var allRecipesWithInventoryIngredients = new List<ModRecipe>();

            for (int i = 0; i < items.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = items[i];
                var recipes = await Task.Run(() => FindRecipesWithIngredient(item.Item));
                allRecipesWithInventoryIngredients.AddRange(recipes);

                CurrentProgress = i + 1;
                CurrentProcessingStep = $"Finding recipes... ({CurrentProgress}/{items.Length} items)";
            }

            // Filter to only recipes that are entirely satisfiable, and remove duplicates by result item name
            CurrentProcessingStep = "Filtering craftable recipes...";
            var craftableRecipes = allRecipesWithInventoryIngredients
                .Where(recipe => CanCraftRecipe(recipe, inventoryCounts))
                .DistinctBy(r => r.Item.Name.ToString()) // Remove duplicates by result item name
                .ToList();

            // Update progress to indicate we're moving to the API phase
            CurrentProcessingStep = "Fetching market prices...";

            // Create cancellation token with 10-second timeout
            using var timedCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var timedCancellationToken = timedCancellationTokenSource.Token;

            // And combine it with our task level cancellation
            using var apiCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(timedCancellationToken, cancellationToken);
            var apiCancellationToken = apiCancellationTokenSource.Token;

            var itemsWithoutValue = craftableRecipes.Select(x => x.Item.RowId).ToList();
            var recipesWithValues = new List<ModRecipeWithValue>();
            // Create a lookup dictionary for quick access to recipes by item ID
            var recipeLookup = craftableRecipes.ToDictionary(r => r.Item.RowId, r => r);

            try
            {
                var marketData = await universalisService.GetMarketDataAsync(itemsWithoutValue, apiCancellationToken);

                foreach (var marketItem in marketData.results ?? [])
                {
                    var id = marketItem.itemId;

                    // Get the recipe for this item
                    if (recipeLookup.TryGetValue(id, out var recipe))
                    {
                        // Get the market value using the service
                        var marketValue = UniversalisService.GetMarketValue(marketItem);

                        // Create the recipe with value
                        var recipeWithValue = new ModRecipeWithValue(
                            recipe,
                            marketValue,
                            gil
                        );

                        recipesWithValues.Add(recipeWithValue);
                    }
                }

                // For now, assume all items were found (simplified - could be enhanced to handle failed items)
                itemsWithoutValue = marketData.failedItems ?? [];
            }
            catch (OperationCanceledException)
            {
                if (timedCancellationToken.IsCancellationRequested)
                {
                    Plugin.Log.Warning("Universalis API request timed out after 10 seconds");
                    itemsWithoutValue = craftableRecipes.Select(x => x.Item.RowId).ToList(); // All items failed due to timeout
                }

                return;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Error calling Universalis API: {ex.Message}");
                itemsWithoutValue = craftableRecipes.Select(x => x.Item.RowId).ToList(); // All items failed due to error
            }

            foreach (var itemId in itemsWithoutValue)
            {
                if (recipeLookup.TryGetValue(itemId, out var recipe))
                {
                    // Check if this item is collectable
                    var (isCollectable, scripType, scripValue) = collectableService.GetCollectableInfo(recipe.Item);
                    if (isCollectable)
                    {
                        recipesWithValues.Add(new ModRecipeWithValue(recipe, scripValue, scripType));
                    }
                    else
                    {
                        // Get the item's store price as a fallback, assuming we make it HQ for a 10% bonus
                        recipesWithValues.Add(new ModRecipeWithValue(recipe, itemSheet.GetRow(itemId).PriceLow * 1.1, gil));
                    }
                }
            }

            // Atomically update the cache
            cachedRecipes = recipesWithValues;
        }
        finally
        {
            isCacheInitializing = false;
        }
    }

    private string GetItemName(uint itemId)
    {
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        if (itemSheet.TryGetRow(itemId, out var itemRow))
        {
            return itemRow.Name.ToString();
        }
        return $"Unknown Item ({itemId})";
    }

    public List<ModItemStack> GetCrystals()
    {
        return GetItemsFromInventory(GameInventoryType.Crystals).ToList();
    }

    public List<ModItemStack> GetConsolidatedItems()
    {
        var allItems = new List<ModItemStack>();

        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();

        // Collect items from all inventory bags (excluding crystals)
        allItems.AddRange(GetItemsFromInventory(GameInventoryType.Inventory1));
        allItems.AddRange(GetItemsFromInventory(GameInventoryType.Inventory2));
        allItems.AddRange(GetItemsFromInventory(GameInventoryType.Inventory3));
        allItems.AddRange(GetItemsFromInventory(GameInventoryType.Inventory4));

        // Consolidate items with the same ID
        var consolidatedItems = allItems
            .GroupBy(stack => stack.Id)
            .Select(group =>
            {
                var firstStack = group.First();
                var totalQuantity = group.Sum(stack => stack.Quantity);
                return new ModItemStack(firstStack.Item, firstStack.Id, totalQuantity);
            })
            .ToList();

        return consolidatedItems;
    }

    private IEnumerable<ModItemStack> GetItemsFromInventory(GameInventoryType inventory)
    {
        var items = new List<ModItemStack>();
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        var gameInventoryItems = Plugin.GameInventory.GetInventoryItems(inventory).ToArray().Where(x => x.ItemId != 0);
        foreach (var item in gameInventoryItems) {
            if (!itemSheet.TryGetRow(item.BaseItemId, out var itemRow))
            {
                continue;
            }

            items.Add(new ModItemStack(itemRow.ToMod(), item.BaseItemId, item.Quantity));
        }

        return items;
    }

    private ModRecipe GetRecipeIngredients(Recipe recipe)
    {
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        var ingredientsDict = new Dictionary<ModItem, byte>();

        try
        {
            // Iterate through both collections simultaneously
            var ingredients = recipe.Ingredient;
            var amounts = recipe.AmountIngredient;

            for (int i = 0; i < Math.Min(ingredients.Count, amounts.Count); i++)
            {
                var ingredientRef = ingredients[i];
                var amount = amounts[i];

                // Check if this ingredient exists and has a positive amount
                if (ingredientRef.RowId != 0 && amount > 0)
                {
                    // Get the actual Item object from the Excel sheet
                    if (itemSheet.TryGetRow(ingredientRef.RowId, out var item))
                    {
                        ingredientsDict[item.ToMod()] = amount;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log error but don't throw - return what we have
            Plugin.Log.Error($"Error getting recipe ingredients: {ex.Message}");
        }

        // Get recipe level info
        var recipeLevelTable = Plugin.DataManager.GetExcelSheet<RecipeLevelTable>();
        var recipeLevel = recipeLevelTable.GetRow(recipe.RecipeLevelTable.RowId);

        // offset of 8 is because 0-7 are the base combat classes in the ClassJob sheet we use later, but craft type starts at 0 since it only contains crafting classes
        return new ModRecipe(recipe.RowId, recipe.ItemResult.Value.ToMod(), recipe.AmountResult, ingredientsDict, recipeLevel.ClassJobLevel, recipe.CraftType.RowId + 8, recipe.SecretRecipeBook.RowId);
    }

    private bool CanCraftRecipe(ModRecipe recipe, Dictionary<uint, int> inventoryCounts)
    {
        // Check if we have enough of each ingredient
        foreach (var ingredient in recipe.Ingredients)
        {
            var ingredientId = ingredient.Key.RowId;

            // Check if we have this item in inventory
            if (!inventoryCounts.TryGetValue(ingredientId, out var availableQuantity))
            {
                return false; // Don't have this ingredient at all
            }

            // Check if we have enough quantity
            if (availableQuantity < ingredient.Value)
            {
                return false; // Don't have enough of this ingredient
            }
        }

        var classJobSheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
        var classJob = classJobSheet.GetRow(recipe.classJobId);

        var playerLevel = Plugin.PlayerState.GetClassJobLevel(classJob);
        if (playerLevel < recipe.classJobLevel)
        {
            Plugin.Log.Debug(GetItemName(recipe.Item.RowId) + " requires level " + recipe.classJobLevel + " " + classJob.Name.ToString() + ". Player level: " + playerLevel);
            return false; // Player level too low
        }

        if (recipe.book > 0)
        {
            unsafe
            {
                if (!PlayerState.Instance()->IsSecretRecipeBookUnlocked(recipe.book)) return false;
            }
        }

        return true; // Have enough of all ingredients
    }

    private readonly Dictionary<ModItem, List<ModRecipe>> ingredientCache = [];
    private List<ModRecipe> FindRecipesWithIngredient(ModItem item)
    {
        if (!ingredientCache.TryGetValue(item, out var cachedRecipes))
        {
            var recipes = FindRecipes().Values.Where(x => x.Ingredients.ContainsKey(item)).ToList();
            ingredientCache[item] = recipes;
            return recipes;
        }

        return cachedRecipes;
    }

    private Dictionary<uint, ModRecipe>? recipeCache;
    public Dictionary<uint, ModRecipe> FindRecipes()
    {
        if (recipeCache == null)
        {
            recipeCache = Plugin.DataManager.GetExcelSheet<Recipe>()
                .Where(x => x.ItemResult.Value.Name != string.Empty)
                .Select(GetRecipeIngredients).ToDictionary(x => x.RowId, x => x);
        }

        return recipeCache;

    }
}
