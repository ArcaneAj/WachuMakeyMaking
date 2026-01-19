using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Lumina.Excel.Sheets;
using SamplePlugin.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SamplePlugin.Services;

public class RecipeCacheService : IDisposable
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

        // Subscribe to inventory changes
        Plugin.GameInventory.InventoryChanged += OnInventoryChanged;
    }

    public void Dispose()
    {
        // Unsubscribe from inventory changes
        Plugin.GameInventory.InventoryChanged -= OnInventoryChanged;
    }

    public bool IsCacheInitializing => isCacheInitializing;

    public string CurrentProcessingStep { get; private set; } = string.Empty;

    public int CurrentProgress { get; private set; }

    public int TotalProgress { get; private set; }

    public List<ModRecipeWithValue> CachedRecipes => cachedRecipes;

    public void ForceRefresh()
    {
        cachedRecipes.Clear();
        isCacheInitializing = false;
        CurrentProcessingStep = string.Empty;
        CurrentProgress = 0;
        TotalProgress = 0;
    }

    public async Task EnsureCacheInitializedAsync()
    {
        if (cachedRecipes.Count == 0 && !isCacheInitializing)
        {
            isCacheInitializing = true;
            await InitializeRecipeCacheAsync();
        }
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        // Clear cache when inventory changes - it will be rebuilt on next access
        cachedRecipes.Clear();
        isCacheInitializing = false; // Reset flag so new cache can be built
        CurrentProcessingStep = string.Empty;
        CurrentProgress = 0;
        TotalProgress = 0;
    }

    private async Task InitializeRecipeCacheAsync()
    {
        try
        {
            CurrentProcessingStep = "Getting inventory items...";
            CurrentProgress = 0;

            // Get consolidated item stacks and crystals
            var consolidatedItems = await Task.Run(GetConsolidatedItems);
            var crystals = await Task.Run(GetCrystals);

            var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
            var gil = itemSheet.GetRow(1);

            // Create inventory count lookup (items + crystals)
            var inventoryCounts = consolidatedItems.ToDictionary(
                stack => stack.Id,
                stack => stack.Quantity
            );

            foreach (var crystal in crystals)
            {
                inventoryCounts[crystal.Id] = crystal.Quantity;
            }

            // Set total progress to the number of consolidated items to process
            TotalProgress = consolidatedItems.Count;
            CurrentProcessingStep = $"Finding recipes... (0/{consolidatedItems.Count} items)";

            // Find all recipes that use at least one ingredient from inventory
            var allRecipesWithInventoryIngredients = new List<ModRecipe>();

            for (int i = 0; i < consolidatedItems.Count; i++)
            {
                var item = consolidatedItems[i];
                var recipes = await Task.Run(() => FindRecipesWithIngredient(item.Item));
                allRecipesWithInventoryIngredients.AddRange(recipes);

                CurrentProgress = i + 1;
                CurrentProcessingStep = $"Finding recipes... ({CurrentProgress}/{consolidatedItems.Count} items)";
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
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var cancellationToken = cancellationTokenSource.Token;

            var itemsWithoutValue = craftableRecipes.Select(x => x.Item.RowId).ToList();
            var recipesWithValues = new List<ModRecipeWithValue>();
            // Create a lookup dictionary for quick access to recipes by item ID
            var recipeLookup = craftableRecipes.ToDictionary(r => r.Item.RowId, r => r);

            try
            {
                var marketData = await universalisService.GetMarketDataAsync(itemsWithoutValue, cancellationToken);

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
                            recipe.Item,
                            recipe.Ingredients,
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
                Plugin.Log.Warning("Universalis API request timed out after 10 seconds");
                itemsWithoutValue = craftableRecipes.Select(x => x.Item.RowId).ToList(); // All items failed due to timeout
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Error calling Universalis API: {ex.Message}");
                itemsWithoutValue = craftableRecipes.Select(x => x.Item.RowId).ToList(); // All items failed due to error
            }

            foreach (var itemId in itemsWithoutValue)
            {
                if (recipeLookup.TryGetValue(itemId, out var item))
                {
                    // Check if this item is collectable
                    var (isCollectable, scripType, scripValue) = collectableService.GetCollectableInfo(item.Item);
                    recipesWithValues.Add(new ModRecipeWithValue(item.Item, item.Ingredients, scripValue, scripType));
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


    private List<ModItemStack> GetCrystals()
    {
        return GetItemsFromInventory(GameInventoryType.Crystals).ToList();
    }

    private List<ModItemStack> GetConsolidatedItems()
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

            items.Add(new ModItemStack(itemRow, item.BaseItemId, item.Quantity));
        }

        return items;
    }

    private ModRecipe GetRecipeIngredients(Recipe recipe)
    {
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        var ingredientsDict = new Dictionary<Item, byte>();

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
                        ingredientsDict[item] = amount;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log error but don't throw - return what we have
            Plugin.Log.Error($"Error getting recipe ingredients: {ex.Message}");
        }

        return new ModRecipe(recipe.ItemResult.Value, ingredientsDict);
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

        return true; // Have enough of all ingredients
    }

    private List<ModRecipe> FindRecipesWithIngredient(Item item)
    {
        var recipeSheet = Plugin.DataManager.GetExcelSheet<Recipe>();

        return recipeSheet.Select(GetRecipeIngredients).Where(x => x.Ingredients.ContainsKey(item)).ToList();
    }
}
